using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private string? _bottleChatId;

    private void InvalidateBottleResearch()
    {
        _bottleChatId = null;
        GingerBottleCard.Visibility = Visibility.Collapsed;
        GingerBottleSources.Children.Clear();
    }

    private void ShowBottleResearch(string chatId, string title)
    {
        _bottleChatId = chatId;
        GingerBottleQuery.Text = title.Length <= 240 ? title : "";
        GingerBottleUrl.Text = "";
        GingerBottleStatus.Text = title.Length <= 240 ? "Можно найти фотографии и сведения об упаковке."
            : "Сократи название для интернет-поиска до 240 символов.";
        GingerBottleSources.Children.Clear();
        GingerBottleCard.Visibility = Visibility.Visible;
        GingerBottleCard.IsExpanded = false;
    }

    private async Task<bool> TryRunBottleResearchFromChatAsync(string message)
    {
        if (!GingerBottleResearch.TryReadCommand(message, out var query)) return false;
        if (_gingerRunning || _agentsDialogOpen || _gingerDialogOpen || ApprovalCard.Visibility == Visibility.Visible)
        {
            ReplyText.Text = "Сначала заверши текущее действие.";
            return true;
        }
        if (query.Length == 0 && _bottleChatId is null)
        {
            AppendConversation("Вы", message);
            AppendConversation("UNIX", "Укажи название: например, «Найди флакон Baldessarini Ambre EDT 90ml». Можно также сначала прочитать текущий товар Ginger.");
            MessageBox.Text = "";
            return true;
        }
        if (query.Length > 0)
        {
            // Named public research needs no Ginger session or selected row.
            InvalidateGingerChatContext();
            SetBusy(true);
            try
            {
                var chatId = (await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString();
                if (string.IsNullOrWhiteSpace(chatId)) throw new InvalidDataException("Текущий чат не определён.");
                ShowBottleResearch(chatId, query);
            }
            catch (Exception ex) { ReplyText.Text = "Поиск не запущен: " + ex.Message; return true; }
            finally { SetBusy(false); }
        }
        await RunBottleResearchAsync(message);
        return true;
    }

    private async void GingerBottleSearch_Click(object sender, RoutedEventArgs e) =>
        await RunBottleResearchAsync("Найди флакон: " + GingerBottleQuery.Text.Trim());

    private async Task RunBottleResearchAsync(string message)
    {
        if (_busy || _gingerRunning || _agentsDialogOpen || _gingerDialogOpen || _bottleChatId is not { } chatId
            || ApprovalCard.Visibility == Visibility.Visible) return;
        var query = string.Join(" ", GingerBottleQuery.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var url = GingerBottleUrl.Text.Trim();
        if (query.Length is < 3 or > 240 || query.Contains("://"))
        {
            GingerBottleStatus.Text = "Нужно название аромата от 3 до 240 символов. Ссылку укажи в отдельном поле.";
            return;
        }
        InvalidateGingerChatMatch();
        StopVoiceListening(updateUi: false);
        _chatCancellation?.Dispose();
        using var cancellation = new CancellationTokenSource();
        _chatCancellation = cancellation;
        var generation = Interlocked.Increment(ref _chatGeneration);
        var selectedChat = _selectedChatId;
        bool IsCurrent() => !cancellation.IsCancellationRequested && generation == Volatile.Read(ref _chatGeneration)
            && _bottleChatId == chatId && selectedChat == _selectedChatId;
        var summary = "";
        SetBusy(true, canStop: true);
        ApplyState(OrbState.Thinking);
        ShowGingerChatStep("Ищу флакон и читаю страницы-источники…", true);
        GingerBottleCard.IsExpanded = true;
        GingerBottleSources.Children.Clear();
        AppendConversation("Вы", message);
        MessageBox.Text = "";
        try
        {
            if ((await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString() != chatId || !IsCurrent())
                throw new OperationCanceledException();
            var raw = await _bridge.ResearchGingerBottleAsync(query, url, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if ((await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString() != chatId || !IsCurrent())
                throw new OperationCanceledException();
            var result = GingerBottleResearch.Parse(raw, query);
            summary = await RenderBottleResearchAsync(result, IsCurrent);
            if (!IsCurrent()) throw new OperationCanceledException();
        }
        catch (OperationCanceledException) { summary = "Поиск флакона остановлен. Поздние результаты не применены."; }
        catch (Exception ex)
        {
            summary = "Проверка флакона не завершена: " + ex.Message;
            StartupDiagnostics.Log("Bottle research failed", ex);
        }
        finally
        {
            if (_bottleChatId == chatId && selectedChat == _selectedChatId)
            {
                if (cancellation.IsCancellationRequested) GingerBottleSources.Children.Clear();
                GingerBottleStatus.Text = summary;
                AppendConversation("UNIX", summary);
                try { await _bridge.RecordGingerChatAsync(chatId, message, summary); }
                catch (Exception ex) { StartupDiagnostics.Log("Bottle research history failed", ex); }
            }
            if (ReferenceEquals(_chatCancellation, cancellation)) _chatCancellation = null;
            ShowGingerChatStep(cancellation.IsCancellationRequested ? "Поиск флакона остановлен." : "Проверка источников завершена.", false);
            ApplyState(OrbState.Idle);
            SetBusy(false);
        }
    }

    private async Task<string> RenderBottleResearchAsync(BottleResearchResult result, Func<bool> isCurrent)
    {
        var text = new StringBuilder("Интернет-проверка: " + result.Query + "\n");
        text.AppendLine("Проверено: " + DateTimeOffset.Parse(result.CheckedAt).ToLocalTime().ToString("g"));
        text.AppendLine(result.Status switch { "sources" => "Найдены страницы для проверки.",
            "no_results" => "Поиск не дал результатов; отсутствие версии не доказано.",
            _ => "Не удалось получить доступные источники." });
        foreach (var source in result.Sources)
        {
            if (!isCurrent()) throw new OperationCanceledException();
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = source.Title, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            panel.Children.Add(new HyperlinkButton { Content = $"{source.Id}: {source.Url.Host} — открыть источник", NavigateUri = source.Url });
            text.AppendLine($"{source.Id}: {source.Title}\n{source.Url}");
            if (source.Image is not null)
            {
                try
                {
                    using var stream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(source.Image);
                        await writer.StoreAsync();
                    }
                    stream.Seek(0);
                    var bitmap = new BitmapImage { DecodePixelWidth = 360, DecodePixelHeight = 240 };
                    await bitmap.SetSourceAsync(stream);
                    if (!isCurrent()) throw new OperationCanceledException();
                    panel.Children.Add(new Image { Source = bitmap, MaxHeight = 240, Stretch = Stretch.Uniform });
                    panel.Children.Add(new TextBlock { Text = "Фото со страницы: проверь, что на нём нужный аромат и объём.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
                    text.AppendLine("Изображение со страницы: " + source.ImageUrl);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { panel.Children.Add(new TextBlock { Text = "Фото не удалось показать; открой источник.", TextWrapping = TextWrapping.Wrap }); }
            }
            foreach (var excerpt in result.Excerpts.Where(e => e.SourceId == source.Id))
            {
                var field = excerpt.Field switch { "design" => "Дизайн", "volume" => "Объём", "concentration" => "Концентрация", "tester" => "Тестер", _ => "Комплектация" };
                var line = $"{field} — выдержка для проверки: «{excerpt.Quote}»";
                panel.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                text.AppendLine(line);
            }
            GingerBottleSources.Children.Add(panel);
        }
        foreach (var warning in result.Warnings) text.AppendLine(warning);
        text.AppendLine("Старый/новый дизайн и соответствие товару ещё нужно подтвердить. Фото не разрешает сопоставление. Автоматическое сравнение изображений пока не подключено.");
        return text.ToString().TrimEnd();
    }
}
