using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private int _gingerDeferredRefreshGeneration;

    private async void GingerDeferredButton_Loaded(object sender, RoutedEventArgs e) =>
        await RefreshGingerDeferredRowsAsync();

    private async Task RefreshGingerDeferredRowsAsync()
    {
        var generation = Interlocked.Increment(ref _gingerDeferredRefreshGeneration);
        try
        {
            var count = await Task.Run(() => new GingerDeferredRows().Read().Count);
            if (_exitRequested || generation != Volatile.Read(ref _gingerDeferredRefreshGeneration)) return;
            GingerDeferredCount.Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            AutomationProperties.SetName(GingerDeferredButton, $"Отложенные позиции: {count}");
            ToolTipService.SetToolTip(GingerDeferredButton, $"Позиций для разбора: {count}");
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Log("Reading deferred Ginger rows failed", ex);
            if (_exitRequested || generation != Volatile.Read(ref _gingerDeferredRefreshGeneration)) return;
            // An unreadable queue must never appear as an empty queue.
            GingerDeferredCount.Text = "!";
            AutomationProperties.SetName(GingerDeferredButton, "Отложенные позиции: ошибка чтения");
            ToolTipService.SetToolTip(GingerDeferredButton, "Не удалось прочитать сохранённые позиции. Открой список для подробностей.");
        }
    }

    private bool GingerDeferredOpeningBlocked() => _exitRequested || _busy || _gingerRunning ||
        _gingerDialogOpen || _agentsDialogOpen || ApprovalCard.Visibility == Visibility.Visible ||
        _gingerChatConfirmation is not null || GingerChatConfirmationCard.Visibility == Visibility.Visible;

    private async void GingerDeferredButton_Click(object sender, RoutedEventArgs e)
    {
        if (GingerDeferredOpeningBlocked())
        {
            ReplyText.Text = "Сначала заверши текущее действие или проверь ожидающее подтверждение.";
            return;
        }
        var originChat = _selectedChatId;
        GingerDeferredRow? revisit = null;
        _gingerDialogOpen = true;
        try
        {
            SetBusy(true);
            // Global identity deduplication preserves the chat that first saved a reminder.
            var rows = await Task.Run(() => new GingerDeferredRows().Read()
                .OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id, StringComparer.Ordinal).ToArray());
            if (_exitRequested) return;
            var introduction = new TextBlock
            {
                Text = rows.Length == 0 ? "Отложенных позиций пока нет." :
                    "Позиции, для которых не нашлось кандидатов. Открой нужный прайс в Ginger и выбери строку для разбора.",
                TextWrapping = TextWrapping.Wrap,
            };
            var list = new ListView
            {
                ItemsSource = rows, DisplayMemberPath = nameof(GingerDeferredRow.Title),
                SelectionMode = ListViewSelectionMode.Single, Height = 240,
                IsItemClickEnabled = false,
                Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
            };
            AutomationProperties.SetName(list, "Сохранённые отложенные позиции");
            var detail = new TextBlock { Text = "Выбери позицию, чтобы увидеть поставщика и документ.",
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible };
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 12, MaxWidth = 520 };
            content.Children.Add(introduction);
            content.Children.Add(list);
            content.Children.Add(new ScrollViewer { Content = detail, MaxHeight = 180,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            content.Children.Add(status);
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = $"Отложенные позиции · {rows.Length}", Content = content,
                PrimaryButtonText = "Открыть для разбора", IsPrimaryButtonEnabled = false,
                CloseButtonText = "Закрыть", DefaultButton = ContentDialogButton.Close,
            };
            list.SelectionChanged += (_, _) =>
            {
                dialog.IsPrimaryButtonEnabled = list.SelectedItem is GingerDeferredRow;
                if (list.SelectedItem is GingerDeferredRow selected)
                    detail.Text = $"{selected.Supplier} · документ {selected.Document} · строка {selected.Position}\n" +
                        $"{selected.Title}\n\n{selected.Reason}\nСохранено: {selected.CreatedAt.ToLocalTime():g}";
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                // These two flags belong to this dialog. Every other guard remains authoritative.
                if (_exitRequested || _gingerRunning || _agentsDialogOpen || ApprovalCard.Visibility == Visibility.Visible ||
                    _gingerChatConfirmation is not null || _selectedChatId != originChat || list.SelectedItem is not GingerDeferredRow selected)
                {
                    args.Cancel = true;
                    status.Text = "Состояние приложения изменилось. Закрой список и заверши текущее действие.";
                    return;
                }
                revisit = selected;
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) revisit = null;
        }
        catch (Exception ex)
        {
            revisit = null;
            ReplyText.Text = "Не удалось открыть отложенные позиции. " + ex.Message;
            StartupDiagnostics.Log("Opening deferred Ginger rows failed", ex);
        }
        finally
        {
            // Release only the reservation acquired at the start; leave every approval untouched.
            _gingerDialogOpen = false;
            _busy = false;
            try { SetBusy(false); }
            catch (Exception ex)
            {
                revisit = null;
                StartupDiagnostics.Log("Restoring controls after deferred Ginger rows failed", ex);
            }
        }
        if (revisit is null || GingerDeferredOpeningBlocked() || _selectedChatId != originChat) return;
        try
        {
            await RunGingerFromChatAsync($"Разобрать отложенную позицию: строка {revisit.Position}.",
                expectedChatId: originChat, revisit: revisit);
        }
        catch (Exception ex)
        {
            ReplyText.Text = "Не удалось открыть позицию для разбора. " + ex.Message;
            StartupDiagnostics.Log("Revisiting a deferred Ginger row failed", ex);
        }
    }

    private async Task<GingerDesktopObservation> ReadGingerChatStartAsync(GingerDesktopSession session,
        GingerDeferredRow? revisit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var fresh = await session.ObserveAsync(token, prepare: true);
        token.ThrowIfCancellationRequested();
        if (revisit is null) return fresh;

        static GingerChatItem Item(GingerDesktopObservation value) => new(value.Supplier, value.Document,
            value.Position, value.Title, value.Linked, value.Candidates);
        var processId = fresh.Data.GetProperty("process_id").GetInt64();
        var table = fresh.Source.GetProperty("TableHandle").GetInt64();
        await Task.Run(async () =>
        {
            // This path belongs to this fresh read, never to the persisted reminder.
            await using var stream = new FileStream(Path.Combine(fresh.Directory, "before.json"),
                FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > 16 * 1024 * 1024)
                throw new InvalidDataException("Не удалось проверить свежий список строк прайса.");
            using var before = await JsonDocument.ParseAsync(stream,
                new JsonDocumentOptions { MaxDepth = 12 }, token);
            GingerDeferredSelection.ValidateBeforeSelection(revisit, Item(fresh), before.RootElement,
                processId, table, fresh.Count);
        }, token);
        token.ThrowIfCancellationRequested();
        var selected = await session.ObserveAsync(token, fresh, position: revisit.Position);
        token.ThrowIfCancellationRequested();
        GingerDeferredSelection.ValidateSelected(revisit, Item(selected));
        if (selected.Data.GetProperty("process_id").GetInt64() != processId ||
            selected.Source.GetProperty("TableHandle").GetInt64() != table || selected.Count != fresh.Count)
            throw new InvalidDataException("Окно или список строк Ginger изменились во время выбора позиции.");
        return selected;
    }
}
