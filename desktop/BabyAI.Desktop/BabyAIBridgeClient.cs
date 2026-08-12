using System.Diagnostics;
using System.Text.Json;

namespace BabyAI.Desktop;

public sealed class BabyAIBridgeClient
{
    public async Task<DesktopStatus> StatusAsync()
    {
        var json = await ExecuteAsync("status", "{}");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var snapshot = root.GetProperty("snapshot");
        var identity = snapshot.GetProperty("identity");
        var learning = snapshot.GetProperty("learning");
        var task = snapshot.TryGetProperty("task", out var taskElement) && taskElement.ValueKind != JsonValueKind.Null
            ? taskElement.GetProperty("goal").GetString()
            : null;
        var requiresApproval = learning.TryGetProperty("lesson", out var lesson)
            && lesson.ValueKind != JsonValueKind.Null;

        return new DesktopStatus(
            identity.GetProperty("name").GetString() ?? "BabyAI",
            task,
            requiresApproval);
    }

    public async Task<string> ChatAsync(string message)
    {
        var payload = JsonSerializer.Serialize(new { message });
        var json = await ExecuteAsync("chat", payload);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("reply").GetString() ?? string.Empty;
    }

    public Task ApproveLessonAsync() => ExecuteAsync("lesson.approve", "{}");

    public Task RejectLessonAsync() => ExecuteAsync("lesson.reject", "{}");

    private static async Task<string> ExecuteAsync(string command, string payload)
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
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "BabyAI bridge failed." : stderr.Trim());

            return stdout.Trim();
        }
    }
}

public sealed record DesktopStatus(string Name, string? TaskGoal, bool RequiresApproval);
