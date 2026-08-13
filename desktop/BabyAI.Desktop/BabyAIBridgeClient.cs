using System.Diagnostics;
using System.Text.Json;

namespace BabyAI.Desktop;

public sealed class BabyAIBridgeClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _workerSync = new();
    private Process? _worker;
    private Task<string>? _workerStderr;
    private long _nextRequestId;
    private bool _disposed;

    public async Task<DesktopStatus> StatusAsync()
    {
        var json = await ExecuteAsync("status", "{}");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var snapshot = root.GetProperty("snapshot");
        var identity = snapshot.GetProperty("identity");
        var learning = snapshot.GetProperty("learning");
        var runtime = snapshot.GetProperty("runtime");
        var task = snapshot.TryGetProperty("task", out var taskElement) && taskElement.ValueKind != JsonValueKind.Null
            ? taskElement.GetProperty("goal").GetString()
            : null;
        var requiresApproval = learning.TryGetProperty("lesson", out var lesson)
            && lesson.ValueKind != JsonValueKind.Null;

        return new DesktopStatus(
            identity.GetProperty("name").GetString() ?? "BabyAI",
            task,
            requiresApproval,
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

    public Task ApproveLessonAsync() => ExecuteAsync("lesson.approve", "{}");

    public Task RejectLessonAsync() => ExecuteAsync("lesson.reject", "{}");

    private async Task<string> ExecuteAsync(
        string command,
        string payload,
        CancellationToken cancellationToken = default)
    {
        await _requestGate.WaitAsync(cancellationToken);
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
                ResetWorker();
                if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw new TimeoutException("BabyAI response timed out after 3 minutes.");
                throw;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                ResetWorker();
                throw new InvalidOperationException("BabyAI desktop worker connection failed.", ex);
            }

            if (response is null)
            {
                var stderr = ReadWorkerError();
                ResetWorker();
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(stderr)
                        ? "BabyAI desktop worker exited unexpectedly."
                        : stderr);
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
                ResetWorker();
                throw new InvalidOperationException("BabyAI desktop worker returned an invalid response.", ex);
            }

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
            };
            startInfo.ArgumentList.Add("-u");
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("babyai.desktop_worker");

            try
            {
                _worker = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Could not start BabyAI Python bridge.");
                _workerStderr = _worker.StandardError.ReadToEndAsync();
                return _worker;
            }
            catch (Exception ex)
            {
                _worker = null;
                _workerStderr = null;
                throw new InvalidOperationException(
                    $"Could not start BabyAI Python bridge using '{python}'. Run scripts/windows/bootstrap.ps1 first.", ex);
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
    bool RequiresApproval,
    BrainStatus Brain);

public sealed record BrainStatus(
    string Provider,
    string Model,
    string State,
    bool Ready,
    string Detail);