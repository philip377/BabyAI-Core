using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BabyAI.Desktop;

// Fixed, bundled read-only probes. Never accepts commands or paths from a model.
internal static class GingerDiagnostics
{
    internal static readonly string[] Probes =
    ["ginger-uia-probe.ps1", "ginger-msaa-probe.ps1", "ginger-listview-probe.ps1"];

    internal static async Task<GingerReadiness> CollectAsync(Action<string> log, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var folder = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "BabyAI", "diagnostics",
            "ginger-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        log("Самостоятельно проверяю доступность таблиц Ginger. Названия товаров не собираю.");
        var results = new List<GingerProbeResult>();
        foreach (var probe in Probes)
        {
            token.ThrowIfCancellationRequested();
            var script = Path.Combine(AppContext.BaseDirectory, "Ginger", probe);
            var status = "failed";
            GingerProbeResult? result = null;
            try
            {
                if (!File.Exists(script))
                    throw new FileNotFoundException("Bundled probe missing", probe);
                var info = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                        "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                };
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script })
                    info.ArgumentList.Add(arg);
                var json = await RunAsync(info, TimeSpan.FromSeconds(30), token);
                using var document = JsonDocument.Parse(json);
                result = GingerReadiness.Parse(probe, document.RootElement);
                await File.WriteAllTextAsync(Path.Combine(folder, Path.GetFileNameWithoutExtension(probe) + ".json"),
                    json, Encoding.UTF8, token);
                status = "collected";
                log("Отчёт о доступности интерфейса сохранён. Это ещё не подтверждение выбранного товара.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or
                System.ComponentModel.Win32Exception or JsonException or OperationCanceledException)
            {
                result = null;
                status = ex is OperationCanceledException ? "timeout" : "failed";
                log(status == "timeout" ? "Один способ чтения не ответил за 30 секунд; пробую следующий."
                    : "Один способ чтения недоступен; продолжаю диагностику.");
            }
            results.Add(result ?? new GingerProbeResult(probe, status));
        }
        var readiness = GingerReadiness.Assess(results);
        // Scalar native messages need no OCR or external model and never send input.
        var selection = await Task.Run(GingerNativeSelection.Inspect, token);
        token.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(Path.Combine(folder, "native-selection.json"),
            JsonSerializer.Serialize(selection, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8, token);
        log(selection.Tables.Any(t => t.Status == "single_index_observed")
            ? "Нативное чтение обнаружило одиночное выделение. Номер строки не является ID товара; запись запрещена."
            : "Нативное чтение не подтвердило одиночное выделение товара; запись запрещена.");
        await File.WriteAllTextAsync(Path.Combine(folder, "summary.json"),
            JsonSerializer.Serialize(new { read_only = true, selection_verified = false, can_write = false,
                state = readiness.State, can_run_ocr = readiness.CanRunOcr, results },
                new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8, token);
        log("Локальная диагностика завершена. Папка отчётов: " + folder);
        log("Отчёты никуда не отправлялись.");
        log(readiness.Message);
        return readiness;
    }

    internal static async Task<string> RunAsync(ProcessStartInfo info, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = info };
        if (!process.Start())
            throw new InvalidOperationException("Probe did not start");
        var stdout = ReadBoundedAsync(process.StandardOutput, deadline);
        var stderr = ReadBoundedAsync(process.StandardError, deadline);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Probe failed");
            return await stdout;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationTokenSource deadline)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), deadline.Token) is var count && count > 0)
        {
            if (text.Length + count > 4_000_000)
            {
                deadline.Cancel();
                throw new InvalidDataException("Probe output exceeded limit");
            }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
