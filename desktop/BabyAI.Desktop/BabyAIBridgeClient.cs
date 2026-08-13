using System.Diagnostics;
using System.Text.Json;

namespace BabyAI.Desktop;

public sealed class BabyAIBridgeClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);

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

    private static async Task<string> ExecuteAsync(
        string command,
        string payload,
        CancellationToken cancellationToken = default)
    {
        var python = Environment.GetEnvironmentVariable("BABYAI_PYTHON");
        if (string.IsNullOrWhiteSpace(python))
            python = "python";

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        ApplySavedNativeAcceleration(startInfo);
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("babyai.desktop_commands_cli");
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(command);
        startInfo.ArgumentList.Add("--payload");
        startInfo.ArgumentList.Add(payload);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start BabyAI Python bridge using '{python}'. Run scripts/windows/bootstrap.ps1 first.", ex);
        }

        using (process ?? throw new InvalidOperationException("Could not start BabyAI Python bridge."))
        using (var timeout = new CancellationTokenSource(RequestTimeout))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                TryKill(process);
                try
                {
                    await process.WaitForExitAsync();
                }
                catch
                {
                    // The process is already being torn down; preserve the cancellation result.
                }

                if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw new TimeoutException("BabyAI response timed out after 3 minutes.");

                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "BabyAI bridge failed." : stderr.Trim());

            return stdout.Trim();
        }
    }

    private static void ApplySavedNativeAcceleration(ProcessStartInfo startInfo)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BABYAI_NATIVE_ACCELERATION")))
            return;

        var stored = new DesktopUiSettingsStore().Load().NativeAcceleration;
        startInfo.Environment["BABYAI_NATIVE_ACCELERATION"] = NormaliseNativeAcceleration(stored);
    }

    private static string NormaliseNativeAcceleration(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "auto" => "auto",
            "vulkan" => "vulkan",
            _ => "cpu",
        };
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
            // Best effort: cancellation still returns control to the desktop UI.
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