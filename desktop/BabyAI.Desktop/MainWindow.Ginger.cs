using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private bool _gingerDialogOpen;
    private bool _gingerRunning;
    private CancellationTokenSource? _gingerCancellation;

    // Visible, timestamped event journal. Stored in the user's profile only.
    // This journal is separate from the model's chain-of-thought and contains
    // only observed actions and the structured outcomes of the OCR reviewer.
    private async void GingerNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gingerDialogOpen)
            return;
        _gingerDialogOpen = true;
        var lines = new List<string>();
        var logBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            MinHeight = 305,
            MaxHeight = 355,
            PlaceholderText = "Здесь появятся понятные записи о проверке Ginger.",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(logBox, ScrollBarVisibility.Auto);
        var hint = new TextBlock
        {
            Text = "Только чтение · CPU OCR · без сопоставления и изменений базы",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.72,
        };
        var start = new Button { Content = "Проверить выделенный товар", MinWidth = 226 };
        var stop = new Button { Content = "Остановить", IsEnabled = false };
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
        };
        controls.Children.Add(start);
        controls.Children.Add(stop);
        var layout = new StackPanel { Spacing = 12, MinWidth = 550 };
        layout.Children.Add(hint);
        layout.Children.Add(controls);
        layout.Children.Add(logBox);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "UNIX · Ginger — журнал действий",
            Content = layout,
            CloseButtonText = "Закрыть",
            DefaultButton = ContentDialogButton.Close,
        };

        void Log(string description)
        {
            var entry = $"[{DateTime.Now:HH:mm:ss}] {description}";
            lines.Add(entry);
            if (lines.Count > 180)
                lines.RemoveAt(0);
            logBox.Text = string.Join(Environment.NewLine, lines);
            logBox.Select(logBox.Text.Length, 0);
            SaveGingerLogLine(entry);
        }

        start.Click += async (_, _) =>
        {
            if (_gingerRunning)
                return;
            _gingerRunning = true;
            start.IsEnabled = false;
            stop.IsEnabled = true;
            _gingerCancellation = new CancellationTokenSource();
            try
            {
                Log("Начинаю проверку. Данные останутся на этом компьютере.");
                await AnalyzeGingerInUnixAsync(Log, _gingerCancellation.Token);
                Log("Проверка завершена. Никаких изменений в Ginger не внесено.");
            }
            catch (OperationCanceledException)
            {
                Log("Проверка остановлена по твоему запросу. Товары не менялись.");
            }
            catch (Exception ex)
            {
                // Keep raw technical exceptions out of the user-facing log.
                StartupDiagnostics.Log("Ginger UI preview failed", ex);
                Log("Проверка остановилась: " + FriendlyGingerError(ex));
            }
            finally
            {
                _gingerCancellation?.Dispose();
                _gingerCancellation = null;
                _gingerRunning = false;
                start.IsEnabled = true;
                stop.IsEnabled = false;
            }
        };
        stop.Click += (_, _) =>
        {
            if (_gingerRunning && _gingerCancellation is { IsCancellationRequested: false })
            {
                Log("Получена команда остановки. Завершаю локальное считывание...");
                _gingerCancellation.Cancel();
            }
        };
        dialog.CloseButtonClick += (_, args) =>
        {
            if (_gingerRunning)
            {
                args.Cancel = true;
                Log("Сначала нажми «Остановить» или дождись завершения чтения.");
            }
        };

        Log("Готов к проверке. Выдели товар поставщика в Ginger и нажми кнопку.");
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _gingerDialogOpen = false;
        }
    }

    private async Task AnalyzeGingerInUnixAsync(Action<string> log, CancellationToken token)
    {
        var script = Path.Combine(
            AppContext.BaseDirectory, "Ginger", "test-ginger-cpu-ocr.ps1");
        if (!File.Exists(script))
            throw new InvalidOperationException("В установленной версии UNIX нет модуля OCR Ginger.");

        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
            throw new InvalidOperationException("Не найден стандартный Windows PowerShell.");

        var info = new ProcessStartInfo
        {
            FileName = powershell,
            WorkingDirectory = Directory.Exists(downloads) ? downloads : AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(script);
        info.ArgumentList.Add("-CaptureSupplierGrid");

        log("Проверяю наличие локального OCR и окна Ginger.");
        using var process = new Process { StartInfo = info };
        // UNIX may be configured to stay above other windows. Lower that
        // priority while Ginger is captured, or the chat overlay can cover
        // supplier cells and contaminate local OCR.
        var presenter = AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        var wasTopmost = presenter?.IsAlwaysOnTop ?? false;
        if (presenter is not null)
            presenter.IsAlwaysOnTop = false;

        string? reportPath = null;
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Не удалось запустить локальное распознавание Ginger.");

            // Read stderr concurrently to avoid filling the child process pipe.
            var stderrTask = process.StandardError.ReadToEndAsync(token);
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                if (line.StartsWith("Switching from PowerShell to Ginger4 window",
                        StringComparison.OrdinalIgnoreCase))
                {
                    log("Нашёл Ginger. Переключаюсь к таблице поставщика.");
                }
                else if (line.StartsWith("CPU OCR completed: ", StringComparison.OrdinalIgnoreCase))
                {
                    reportPath = line["CPU OCR completed: ".Length..].Trim();
                    log("Снимки таблиц сохранены локально; распознавание завершено.");
                }
                else if (line.StartsWith("Left lines: ", StringComparison.OrdinalIgnoreCase))
                {
                    var counts = Regex.Match(
                        line, @"Left lines: (\d+); right lines: (\d+); upper lines: (\d+)");
                    if (counts.Success)
                        log($"Прочитал области Ginger: слов — {counts.Groups[1].Value}, "
                            + $"кандидатов — {counts.Groups[2].Value}, строк OCR сверху — {counts.Groups[3].Value}.");
                }
            }

            await process.WaitForExitAsync(token);
            var errors = await stderrTask;
            if (process.ExitCode != 0)
            {
                StartupDiagnostics.Log(
                    "Ginger OCR subprocess failed: " + TruncateGingerLog(errors, 1200));
                throw new InvalidOperationException(
                    "OCR не смог прочитать окно. Проверь, что Ginger открыт, а Tesseract установлен.");
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            if (presenter is not null)
                presenter.IsAlwaysOnTop = wasTopmost;
            Activate();
        }

        if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
            throw new InvalidOperationException("OCR не вернул новый отчёт. Старые данные не использую.");

        token.ThrowIfCancellationRequested();
        // Focus returns only AFTER screen capture has finished.
        Activate();
        log("Проверяю распознанные названия и возможные совпадения.");
        var review = await _bridge.ReviewGingerAsync(reportPath, token);
        token.ThrowIfCancellationRequested();
        RenderGingerReviewLog(review, log);
    }

    private static void RenderGingerReviewLog(JsonElement review, Action<string> log)
    {
        if (!review.TryGetProperty("selected_supplier_as_read", out var selected)
            || selected.ValueKind != JsonValueKind.String)
        {
            log("Не смог надёжно определить выделенную строку. Выбери один товар и повтори проверку.");
            log("Никакие сопоставления и награды не выполнялись.");
            return;
        }

        var supplierTitle = TruncateGingerLog(selected.GetString() ?? "", 180);
        log("Товар поставщика: " + supplierTitle);
        if (!review.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array)
        {
            log("Каталог пока не удалось прочитать.");
            return;
        }

        var index = 0;
        foreach (var candidate in candidates.EnumerateArray())
        {
            index++;
            var title = candidate.GetProperty("title_as_read").GetString() ?? "";
            var status = candidate.GetProperty("status").GetString() ?? "";
            var explanation = status switch
            {
                "unreadable_ocr" => "НЕ УГАДЫВАЮ: название обрезано или распознано с ошибками.",
                "tester_mismatch" => "НЕ ПОДХОДИТ: тестер и обычный флакон отличаются.",
                "different_brand" => "НЕ ПОДХОДИТ: отличается бренд.",
                "variant_mismatch" => "НЕ ПОДХОДИТ: отличается версия аромата.",
                "concentration_mismatch" => "НЕ ПОДХОДИТ: отличается концентрация.",
                "volume_mismatch" => "НЕ ПОДХОДИТ: отличается объём.",
                "name_mismatch" => "НУЖНА ПРОВЕРКА: название отличается.",
                "exact_visible_text_review" => "ВОЗМОЖНО СОВПАДЕНИЕ: текст похож, но ID товара не проверен.",
                _ => "НУЖНА ПРОВЕРКА: данных недостаточно для уверенного выбора.",
            };
            log($"{index}. {TruncateGingerLog(title, 145)}");
            log("     " + explanation);
        }

        if (index == 0)
            log("Справа не найдено читаемых вариантов для сопоставления.");
        log("Итог: автоматическая привязка и создание товара выключены.");
        log("Баллы: 0 — без подтверждённого действия награда не начисляется.");
    }

    private static string TruncateGingerLog(string text, int max)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string FriendlyGingerError(Exception error)
    {
        if (error is TimeoutException)
            return "превышено время ожидания локального модуля.";
        if (error is FileNotFoundException)
            return "не найден необходимый локальный файл OCR.";
        if (error is OperationCanceledException)
            return "проверка отменена.";
        if (error is InvalidOperationException && error.Message.StartsWith("OCR"))
            return error.Message;
        if (error is InvalidOperationException &&
            (error.Message.StartsWith("В установленной") ||
             error.Message.StartsWith("Не найден стандартный")))
            return error.Message;
        return "не удалось завершить анализ. Подробности доступны в журнале диагностики UNIX.";
    }

    private static void SaveGingerLogLine(string entry)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BabyAI", "logs");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "ginger-actions.log");
            // Keep at most one rotated backup. All log data stays local.
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
            {
                var backup = Path.Combine(folder, "ginger-actions.previous.log");
                if (File.Exists(backup))
                    File.Delete(backup);
                File.Move(path, backup);
            }
            File.AppendAllText(path, entry + Environment.NewLine, Encoding.UTF8);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
