using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BabyAI.Desktop;

public sealed class BabyAIBridgeClient : IDisposable
{
    private const int StreamingProtocolVersion = 2;
    private const int MaxStreamingReplyChars = 1_048_576;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _workerSync = new();
    private Process? _worker;
    private Task<string>? _workerStderr;
    private long _nextRequestId;
    private bool _disposed;

    public async Task<DesktopStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        var json = await ExecuteAsync("status", "{}", cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var snapshot = root.GetProperty("snapshot");
        var identity = snapshot.GetProperty("identity");
        var learning = snapshot.GetProperty("learning");
        var runtime = snapshot.GetProperty("runtime");
        var task = snapshot.TryGetProperty("task", out var taskElement) && taskElement.ValueKind != JsonValueKind.Null
            ? taskElement.GetProperty("goal").GetString()
            : null;
        var taskProject = taskElement.ValueKind != JsonValueKind.Undefined
            && taskElement.ValueKind != JsonValueKind.Null
            && taskElement.TryGetProperty("project", out var projectElement)
                ? projectElement.GetString()
                : null;
        var hasLessonApproval = learning.TryGetProperty("lesson", out var lesson)
            && lesson.ValueKind != JsonValueKind.Null;
        var hasToolApproval = learning.TryGetProperty("tool_approval", out var toolApproval)
            && toolApproval.ValueKind != JsonValueKind.Null;
        var requiresApproval = hasLessonApproval || hasToolApproval;
        var approvalPrompt = hasToolApproval
            && toolApproval.TryGetProperty("prompt", out var promptElement)
                ? promptElement.GetString()
                : hasLessonApproval ? "Сохранить предложенный урок в долговременную память?" : null;
        var historyEnabled = snapshot.TryGetProperty("history", out var history)
            && history.TryGetProperty("enabled", out var enabledElement)
            && enabledElement.GetBoolean();
        var historyCount = history.ValueKind != JsonValueKind.Undefined
            && history.TryGetProperty("message_count", out var countElement)
                ? countElement.GetInt32()
                : 0;

        return new DesktopStatus(
            identity.GetProperty("name").GetString() ?? "BabyAI",
            task,
            taskProject,
            requiresApproval,
            approvalPrompt,
            historyEnabled,
            historyCount,
            new BrainStatus(
                runtime.GetProperty("provider").GetString() ?? "unknown",
                runtime.GetProperty("model").GetString() ?? "unknown",
                runtime.GetProperty("state").GetString() ?? "unknown",
                runtime.GetProperty("ready").GetBoolean(),
                runtime.TryGetProperty("detail", out var detail) ? detail.GetString() ?? string.Empty : string.Empty));
    }

    public async Task<string> ChatAsync(string message, CancellationToken cancellationToken = default)
    {
        var status = await StatusAsync();
        if (!status.Brain.Ready)
        {
            var detail = string.IsNullOrWhiteSpace(status.Brain.Detail)
                ? "The configured local brain is not ready."
                : status.Brain.Detail;
            throw new InvalidOperationException($"Brain not ready ({status.Brain.State}): {detail}");
        }

        var payload = JsonSerializer.Serialize(new { message });
        var json = await ExecuteAsync("chat", payload, cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("reply").GetString() ?? string.Empty;
    }

    public async Task<DesktopChatResult> ChatStreamAsync(
        string message,
        Func<DesktopChatEvent, ValueTask> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onEvent);

        var status = await StatusAsync(cancellationToken);
        if (!status.Brain.Ready)
        {
            var detail = string.IsNullOrWhiteSpace(status.Brain.Detail)
                ? "The configured local brain is not ready."
                : status.Brain.Detail;
            throw new InvalidOperationException($"Brain not ready ({status.Brain.State}): {detail}");
        }

        var payload = JsonSerializer.Serialize(new { message });
        return await ExecuteStreamingAsync("chat", payload, onEvent, cancellationToken);
    }

    public Task ApproveLessonAsync() => ExecuteAsync("lesson.approve", "{}");

    public Task RejectLessonAsync() => ExecuteAsync("lesson.reject", "{}");

    public Task<string> ApproveToolAsync(CancellationToken cancellationToken = default) =>
        ExecuteReplyCommandAsync("approval.approve", cancellationToken);

    public Task<string> RejectToolAsync(CancellationToken cancellationToken = default) =>
        ExecuteReplyCommandAsync("approval.reject", cancellationToken);

    public Task SetHistoryEnabledAsync(bool enabled) =>
        ExecuteAsync("history.set_enabled", JsonSerializer.Serialize(new { enabled }));

    public Task ClearHistoryAsync(string? project = null) =>
        ExecuteAsync("history.clear", JsonSerializer.Serialize(new { project }));

    public void RestartWorker()
    {
        lock (_workerSync)
        {
            ThrowIfDisposed();
            DisposeWorkerLocked();
        }
    }

    private async Task<string> ExecuteReplyCommandAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        var json = await ExecuteAsync(command, "{}", cancellationToken);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("reply", out var reply)
            ? reply.GetString() ?? string.Empty
            : string.Empty;
    }

    private async Task<string> ExecuteAsync(
        string command,
        string payload,
        CancellationToken cancellationToken = default)
    {
        await _requestGate.WaitAsync(cancellationToken);
        var requestWatch = Stopwatch.StartNew();
        try
        {
            ThrowIfDisposed();
            using var timeout = new CancellationTokenSource(RequestTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var worker = EnsureWorker();
            var requestId = Interlocked.Increment(ref _nextRequestId);
            StartupDiagnostics.Log($"Bridge request start: id={requestId}; command={command}");

            using var payloadDocument = JsonDocument.Parse(payload);
            if (payloadDocument.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("BabyAI desktop payload must be a JSON object.");

            var request = JsonSerializer.Serialize(new
            {
                id = requestId,
                command,
                payload = payloadDocument.RootElement.Clone(),
            });

            string? response;
            try
            {
                await worker.StandardInput.WriteLineAsync(request);
                await worker.StandardInput.FlushAsync();
                response = await worker.StandardOutput.ReadLineAsync(linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                StartupDiagnostics.Log(
                    $"Bridge request cancelled: id={requestId}; command={command}; elapsed_ms={requestWatch.ElapsedMilliseconds}; timeout={timeout.IsCancellationRequested}");
                ResetWorker();
                if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw new TimeoutException("BabyAI response timed out after 3 minutes. See native-runtime.log for the last native stage.");
                throw;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                StartupDiagnostics.Log(
                    $"Bridge request transport failed: id={requestId}; command={command}; elapsed_ms={requestWatch.ElapsedMilliseconds}",
                    ex);
                ResetWorker();
                throw new InvalidOperationException("BabyAI desktop worker connection failed.", ex);
            }

            if (response is null)
            {
                var stderr = ReadWorkerError();
                var exitCode = TryReadExitCode(worker);
                var stderrTail = DiagnosticTail(stderr);
                StartupDiagnostics.Log(
                    $"Bridge worker exited: id={requestId}; command={command}; elapsed_ms={requestWatch.ElapsedMilliseconds}; "
                    + $"exit_code={exitCode}; stderr_tail={stderrTail}");
                ResetWorker();
                throw new InvalidOperationException(
                    $"BabyAI desktop worker exited unexpectedly (exit code {exitCode}). "
                    + "See desktop-startup.log and native-runtime.log for diagnostics.");
            }

            try
            {
                using var responseDocument = JsonDocument.Parse(response);
                var root = responseDocument.RootElement;
                if (!root.TryGetProperty("id", out var idElement) || idElement.GetInt64() != requestId)
                    throw new JsonException("BabyAI desktop worker returned an unexpected request id.");

                if (root.TryGetProperty("ok", out var okElement) && !okElement.GetBoolean())
                {
                    var error = root.TryGetProperty("error", out var errorElement)
                        ? errorElement.GetString()
                        : null;
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(error) ? "BabyAI desktop command failed." : error);
                }
            }
            catch (JsonException ex)
            {
                StartupDiagnostics.Log(
                    $"Bridge invalid response: id={requestId}; command={command}; elapsed_ms={requestWatch.ElapsedMilliseconds}",
                    ex);
                ResetWorker();
                throw new InvalidOperationException("BabyAI desktop worker returned an invalid response.", ex);
            }

            StartupDiagnostics.Log(
                $"Bridge request done: id={requestId}; command={command}; elapsed_ms={requestWatch.ElapsedMilliseconds}");
            return response;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private Process EnsureWorker()
    {
        lock (_workerSync)
        {
            ThrowIfDisposed();
            if (_worker is not null && !_worker.HasExited)
                return _worker;

            DisposeWorkerLocked();

            var python = Environment.GetEnvironmentVariable("BABYAI_PYTHON");
            if (string.IsNullOrWhiteSpace(python))
                python = "python";

            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            startInfo.ArgumentList.Add("-u");
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("babyai.desktop_worker");

            var runtimeLog = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BabyAI",
                "logs",
                "native-runtime.log");
            Directory.CreateDirectory(Path.GetDirectoryName(runtimeLog)!);
            startInfo.Environment["BABYAI_RUNTIME_LOG"] = runtimeLog;
            startInfo.Environment["PYTHONUTF8"] = "1";
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

            try
            {
                _worker = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Could not start BabyAI Python bridge.");
                StartupDiagnostics.Log($"Desktop worker started: pid={_worker.Id}; runtime_log={runtimeLog}");
                _workerStderr = _worker.StandardError.ReadToEndAsync();
                return _worker;
            }
            catch (Exception ex)
            {
                _worker = null;
                _workerStderr = null;
                throw new InvalidOperationException(
                    $"Could not start BabyAI Python bridge using '{python}'.", ex);
            }
        }
    }

    private string ReadWorkerError()
    {
        lock (_workerSync)
        {
            if (_workerStderr is null || !_workerStderr.IsCompletedSuccessfully)
                return string.Empty;
            return _workerStderr.Result.Trim();
        }
    }

    private static string TryReadExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode.ToString() : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private async Task<DesktopChatResult> ExecuteStreamingAsync(
        string command,
        string payload,
        Func<DesktopChatEvent, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        var operationWatch = Stopwatch.StartNew();
        try
        {
            ThrowIfDisposed();
            using var timeout = new CancellationTokenSource(RequestTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var worker = EnsureWorker();
            var requestId = Interlocked.Increment(ref _nextRequestId);

            using var payloadDocument = JsonDocument.Parse(payload);
            if (payloadDocument.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("BabyAI desktop payload must be a JSON object.");

            var request = JsonSerializer.Serialize(new
            {
                id = requestId,
                protocol = StreamingProtocolVersion,
                command,
                payload = payloadDocument.RootElement.Clone(),
            });

            StartupDiagnostics.Log(
                $"Bridge stream start: id={requestId}; command={command}; protocol={StreamingProtocolVersion}");

            var streamWatch = new Stopwatch();
            try
            {
                streamWatch.Start();
                await worker.StandardInput.WriteLineAsync(request);
                await worker.StandardInput.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                StartupDiagnostics.Log(
                    $"Bridge stream transport failed: id={requestId}; command={command}; elapsed_ms={operationWatch.ElapsedMilliseconds}",
                    ex);
                ResetWorker();
                throw new InvalidOperationException("BabyAI desktop worker connection failed.", ex);
            }

            long expectedSequence = 0;
            long? firstDeltaMs = null;
            var streamedReply = new StringBuilder();
            var sawV2Event = false;

            while (true)
            {
                string? response;
                try
                {
                    response = await worker.StandardOutput.ReadLineAsync(linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    StartupDiagnostics.Log(
                        $"Bridge stream cancelled: id={requestId}; command={command}; elapsed_ms={operationWatch.ElapsedMilliseconds}; timeout={timeout.IsCancellationRequested}");
                    ResetWorker();
                    if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            "BabyAI response timed out after 3 minutes. See native-runtime.log for the last native stage.");
                    }
                    throw;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    StartupDiagnostics.Log(
                        $"Bridge stream transport failed: id={requestId}; command={command}; elapsed_ms={operationWatch.ElapsedMilliseconds}",
                        ex);
                    ResetWorker();
                    throw new InvalidOperationException("BabyAI desktop worker connection failed.", ex);
                }

                if (response is null)
                {
                    var stderr = ReadWorkerError();
                    var exitCode = TryReadExitCode(worker);
                    StartupDiagnostics.Log(
                        $"Bridge stream worker exited: id={requestId}; command={command}; elapsed_ms={operationWatch.ElapsedMilliseconds}; "
                        + $"exit_code={exitCode}; stderr_tail={DiagnosticTail(stderr)}");
                    ResetWorker();
                    throw new InvalidOperationException(
                        $"BabyAI desktop worker exited unexpectedly (exit code {exitCode}). "
                        + "See desktop-startup.log and native-runtime.log for diagnostics.");
                }

                try
                {
                    using var responseDocument = JsonDocument.Parse(response);
                    var root = responseDocument.RootElement;
                    ValidateResponseId(root, requestId);

                    var hasProtocol = root.TryGetProperty("protocol", out _);
                    var hasEvent = root.TryGetProperty("event", out _);
                    if (!hasProtocol && !hasEvent)
                    {
                        if (sawV2Event)
                            throw new JsonException("BabyAI desktop worker mixed v1 and v2 responses.");

                        var reply = ReadLegacyReply(root);
                        if (reply.Length > MaxStreamingReplyChars)
                            throw new JsonException("BabyAI desktop streaming reply exceeded the size limit.");
                        await InvokeStreamCallbackAsync(
                            onEvent,
                            new DesktopChatEvent(
                                DesktopChatEventKind.State,
                                0,
                                DesktopChatState.Answering,
                                string.Empty,
                                streamWatch.ElapsedMilliseconds,
                                false),
                            linked.Token,
                            requestId,
                            command);

                        if (reply.Length > 0)
                        {
                            firstDeltaMs = streamWatch.ElapsedMilliseconds;
                            await InvokeStreamCallbackAsync(
                                onEvent,
                                new DesktopChatEvent(
                                    DesktopChatEventKind.Delta,
                                    1,
                                    null,
                                    reply,
                                    firstDeltaMs.Value,
                                    true),
                                linked.Token,
                                requestId,
                                command);
                            StartupDiagnostics.Log(
                                $"Bridge stream first delta: id={requestId}; protocol=1; e2e_ttft_ms={firstDeltaMs.Value}");
                        }

                        var legacyMetrics = new DesktopChatMetrics(
                            firstDeltaMs,
                            null,
                            null,
                            streamWatch.ElapsedMilliseconds,
                            null,
                            null,
                            null,
                            "legacy_complete",
                            1);
                        StartupDiagnostics.Log(
                            $"Bridge stream done: id={requestId}; protocol=1; total_ms={legacyMetrics.TotalMs}; "
                            + $"e2e_ttft_ms={Metric(firstDeltaMs)}; stop_reason={legacyMetrics.StopReason}");
                        return new DesktopChatResult(reply, legacyMetrics);
                    }

                    sawV2Event = true;
                    ValidateProtocolVersion(root);
                    var sequence = ReadRequiredInt64(root, "seq");
                    if (sequence != expectedSequence)
                    {
                        throw new JsonException(
                            $"BabyAI desktop worker returned stream sequence {sequence}; expected {expectedSequence}.");
                    }
                    expectedSequence++;

                    var eventName = ReadRequiredString(root, "event");
                    switch (eventName)
                    {
                        case "state":
                        {
                            var state = ReadChatState(root);
                            await InvokeStreamCallbackAsync(
                                onEvent,
                                new DesktopChatEvent(
                                    DesktopChatEventKind.State,
                                    sequence,
                                    state,
                                    string.Empty,
                                    streamWatch.ElapsedMilliseconds,
                                    false),
                                linked.Token,
                                requestId,
                                command);
                            break;
                        }
                        case "delta":
                        {
                            var text = ReadRequiredString(root, "text");
                            if (text.Length == 0)
                                throw new JsonException("BabyAI desktop stream delta text must not be empty.");
                            if (streamedReply.Length + text.Length > MaxStreamingReplyChars)
                                throw new JsonException("BabyAI desktop streaming reply exceeded the size limit.");

                            streamedReply.Append(text);
                            var isFirstDelta = firstDeltaMs is null && text.Length > 0;
                            if (isFirstDelta)
                                firstDeltaMs = streamWatch.ElapsedMilliseconds;

                            await InvokeStreamCallbackAsync(
                                onEvent,
                                new DesktopChatEvent(
                                    DesktopChatEventKind.Delta,
                                    sequence,
                                    null,
                                    text,
                                    streamWatch.ElapsedMilliseconds,
                                    isFirstDelta),
                                linked.Token,
                                requestId,
                                command);

                            if (isFirstDelta)
                            {
                                StartupDiagnostics.Log(
                                    $"Bridge stream first delta: id={requestId}; protocol={StreamingProtocolVersion}; e2e_ttft_ms={firstDeltaMs}");
                            }
                            break;
                        }
                        case "done":
                        {
                            if (!ReadRequiredBoolean(root, "ok"))
                                throw new JsonException("BabyAI desktop done event must have ok=true.");

                            var reply = ReadRequiredString(root, "reply");
                            if (reply.Length > MaxStreamingReplyChars)
                                throw new JsonException("BabyAI desktop streaming reply exceeded the size limit.");

                            var wireMetrics = ReadWireMetrics(root);
                            if (!string.Equals(streamedReply.ToString(), reply, StringComparison.Ordinal))
                            {
                                StartupDiagnostics.Log(
                                    $"Bridge stream canonical reply replaced accumulated deltas: id={requestId}; "
                                    + $"delta_chars={streamedReply.Length}; reply_chars={reply.Length}");
                            }

                            var metrics = new DesktopChatMetrics(
                                firstDeltaMs,
                                wireMetrics.VisibleTtftMs,
                                wireMetrics.FirstTokenMs,
                                streamWatch.ElapsedMilliseconds,
                                wireMetrics.GeneratedTokens,
                                wireMetrics.DeltaCount,
                                wireMetrics.ModelCalls,
                                wireMetrics.StopReason,
                                StreamingProtocolVersion);
                            StartupDiagnostics.Log(
                                $"Bridge stream done: id={requestId}; protocol={StreamingProtocolVersion}; total_ms={metrics.TotalMs}; "
                                + $"worker_total_ms={Metric(wireMetrics.TotalMs)}; worker_visible_ttft_ms={Metric(wireMetrics.VisibleTtftMs)}; "
                                + $"e2e_ttft_ms={Metric(firstDeltaMs)}; "
                                + $"native_first_token_ms={Metric(metrics.NativeFirstTokenMs)}; generated_tokens={Metric(metrics.GeneratedTokens)}; "
                                + $"stop_reason={metrics.StopReason}");
                            return new DesktopChatResult(reply, metrics);
                        }
                        case "error":
                        {
                            if (ReadRequiredBoolean(root, "ok"))
                                throw new JsonException("BabyAI desktop error event must have ok=false.");
                            var error = ReadRequiredString(root, "error");
                            throw new DesktopStreamCommandException(
                                string.IsNullOrWhiteSpace(error) ? "BabyAI desktop command failed." : error);
                        }
                        default:
                            throw new JsonException($"Unknown BabyAI desktop stream event: {eventName}");
                    }
                }
                catch (DesktopStreamCommandException ex)
                {
                    StartupDiagnostics.Log(
                        $"Bridge stream command failed: id={requestId}; command={command}; elapsed_ms={operationWatch.ElapsedMilliseconds}");
                    throw new InvalidOperationException(ex.Message, ex);
                }
                catch (JsonException ex)
                {
                    StartupDiagnostics.Log(
                        $"Bridge invalid stream response: id={requestId}; command={command}; elapsed_ms={operationWatch.ElapsedMilliseconds}",
                        ex);
                    ResetWorker();
                    throw new InvalidOperationException("BabyAI desktop worker returned an invalid streaming response.", ex);
                }
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task InvokeStreamCallbackAsync(
        Func<DesktopChatEvent, ValueTask> onEvent,
        DesktopChatEvent streamEvent,
        CancellationToken cancellationToken,
        long requestId,
        string command)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await onEvent(streamEvent);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ResetWorker();
            throw;
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log(
                $"Bridge stream callback failed: id={requestId}; command={command}",
                ex);
            ResetWorker();
            throw;
        }
    }

    private static string ReadLegacyReply(JsonElement root)
    {
        if (root.TryGetProperty("ok", out var okElement))
        {
            if (okElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("BabyAI desktop worker returned an invalid ok value.");
            if (!okElement.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var errorElement)
                    && errorElement.ValueKind == JsonValueKind.String
                        ? errorElement.GetString()
                        : null;
                throw new DesktopStreamCommandException(
                    string.IsNullOrWhiteSpace(error) ? "BabyAI desktop command failed." : error);
            }
        }

        return ReadRequiredString(root, "reply");
    }

    private static void ValidateResponseId(JsonElement root, long requestId)
    {
        var returnedId = ReadRequiredInt64(root, "id");
        if (returnedId != requestId)
            throw new JsonException("BabyAI desktop worker returned an unexpected request id.");
    }

    private static void ValidateProtocolVersion(JsonElement root)
    {
        var protocol = ReadRequiredInt64(root, "protocol");
        if (protocol != StreamingProtocolVersion)
            throw new JsonException($"Unsupported BabyAI desktop stream protocol: {protocol}");
    }

    private static DesktopChatState ReadChatState(JsonElement root) =>
        ReadRequiredString(root, "state") switch
        {
            "thinking" => DesktopChatState.Thinking,
            "answering" => DesktopChatState.Answering,
            "executing" => DesktopChatState.Executing,
            var state => throw new JsonException($"Unknown BabyAI desktop stream state: {state}"),
        };

    private static WireMetrics ReadWireMetrics(JsonElement root)
    {
        if (!root.TryGetProperty("metrics", out var metrics))
            return new WireMetrics(null, null, null, null, null, null, null, "completed");
        if (metrics.ValueKind != JsonValueKind.Object)
            throw new JsonException("BabyAI desktop stream metrics must be a JSON object.");

        return new WireMetrics(
            ReadOptionalInt64(metrics, "visible_ttft_ms"),
            ReadOptionalInt64(metrics, "native_first_token_ms")
                ?? ReadOptionalInt64(metrics, "first_token_ms"),
            ReadOptionalInt64(metrics, "generation_ms"),
            ReadOptionalInt64(metrics, "total_ms"),
            ReadOptionalInt32(metrics, "generated_tokens"),
            ReadOptionalInt32(metrics, "delta_count"),
            ReadOptionalInt32(metrics, "model_calls"),
            ReadOptionalString(metrics, "stop_reason") ?? "completed");
    }

    private static long ReadRequiredInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out var value)
            || value < 0)
        {
            throw new JsonException($"BabyAI desktop stream field '{name}' must be a non-negative integer.");
        }
        return value;
    }

    private static bool ReadRequiredBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element)
            || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new JsonException($"BabyAI desktop stream field '{name}' must be a boolean.");
        }
        return element.GetBoolean();
    }

    private static string ReadRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            throw new JsonException($"BabyAI desktop stream field '{name}' must be a string.");
        return element.GetString() ?? string.Empty;
    }

    private static long? ReadOptionalInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out var value)
            || value < 0)
        {
            throw new JsonException($"BabyAI desktop stream metric '{name}' must be a non-negative integer or null.");
        }
        return value;
    }

    private static int? ReadOptionalInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value)
            || value < 0)
        {
            throw new JsonException($"BabyAI desktop stream metric '{name}' must be a non-negative integer or null.");
        }
        return value;
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.String)
            throw new JsonException($"BabyAI desktop stream metric '{name}' must be a string or null.");
        return element.GetString();
    }

    private static string Metric(long? value) => value?.ToString() ?? "none";

    private static string Metric(int? value) => value?.ToString() ?? "none";

    private sealed record WireMetrics(
        long? VisibleTtftMs,
        long? FirstTokenMs,
        long? GenerationMs,
        long? TotalMs,
        int? GeneratedTokens,
        int? DeltaCount,
        int? ModelCalls,
        string StopReason);

    private sealed class DesktopStreamCommandException(string message) : Exception(message);

    private static string DiagnosticTail(string value)
    {
        const int limit = 1_200;
        value = value.Trim();
        if (value.Length > limit)
            value = value[^limit..];
        return string.IsNullOrWhiteSpace(value)
            ? "none"
            : value.Replace("\r", " ").Replace("\n", " | ");
    }

    private void ResetWorker()
    {
        lock (_workerSync)
            DisposeWorkerLocked();
    }

    private void DisposeWorkerLocked()
    {
        var process = _worker;
        _worker = null;
        _workerStderr = null;
        if (process is null)
            return;

        TryKill(process);
        process.Dispose();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort. A later request starts a clean worker.
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BabyAIBridgeClient));
    }

    public void Dispose()
    {
        lock (_workerSync)
        {
            if (_disposed)
                return;
            _disposed = true;
            DisposeWorkerLocked();
        }
    }
}

public sealed record DesktopStatus(
    string Name,
    string? TaskGoal,
    string? TaskProject,
    bool RequiresApproval,
    string? ApprovalPrompt,
    bool HistoryEnabled,
    int HistoryCount,
    BrainStatus Brain);

public sealed record BrainStatus(
    string Provider,
    string Model,
    string State,
    bool Ready,
    string Detail);

public enum DesktopChatEventKind
{
    State,
    Delta,
}

public enum DesktopChatState
{
    Thinking,
    Answering,
    Executing,
}

public sealed record DesktopChatEvent(
    DesktopChatEventKind Kind,
    long Sequence,
    DesktopChatState? State,
    string Text,
    long ElapsedMilliseconds,
    bool IsFirstDelta);

public sealed record DesktopChatMetrics(
    long? EndToEndTtftMs,
    long? WorkerVisibleTtftMs,
    long? NativeFirstTokenMs,
    long TotalMs,
    int? GeneratedTokens,
    int? DeltaCount,
    int? ModelCalls,
    string StopReason,
    int ProtocolVersion);

public sealed record DesktopChatResult(
    string Reply,
    DesktopChatMetrics Metrics);
