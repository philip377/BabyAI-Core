using Microsoft.UI.Xaml;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    // These results never contain or restore a matching permission. Conversation
    // focus is session-only and is cleared by other topics and navigation.
    private readonly Dictionary<string, GingerChatTaskResult> _gingerTaskResults = new(StringComparer.Ordinal);
    private readonly HashSet<string> _gingerTaskResultSaveFailures = new(StringComparer.Ordinal);
    private string? _gingerFollowupChatId;
    private bool _gingerFollowupHasTask;

    private void ClearGingerFollowUp()
    {
        _gingerFollowupChatId = null;
        _gingerFollowupHasTask = false;
    }

    private void RecordGingerTaskResult(string? chatId, string runId, DateTimeOffset started,
        GingerChatTaskState state, int deferredCount, string summary)
    {
        if (string.IsNullOrWhiteSpace(chatId)) return;
        var result = new GingerChatTaskResult(chatId, runId, started, DateTimeOffset.UtcNow,
            state, deferredCount, summary);
        // Keep the actual latest result even if persistence fails. Do not fall
        // back to an older successful disk record in this application session.
        _gingerTaskResults[chatId] = result;
        if (chatId == _selectedChatId)
        {
            _gingerFollowupChatId = chatId;
            _gingerFollowupHasTask = true;
        }
        try
        {
            new GingerChatTaskStatusStore().Save(result);
            _gingerTaskResultSaveFailures.Remove(chatId);
        }
        catch (Exception ex)
        {
            _gingerTaskResultSaveFailures.Add(chatId);
            StartupDiagnostics.Log("Ginger task result persistence failed", ex);
        }
    }

    private async Task<bool> TryRouteGingerFollowUpAsync(string message)
    {
        var selectedChat = _selectedChatId;
        var sameChatContext = selectedChat is not null && _gingerFollowupChatId == selectedChat;
        var route = GingerChatFollowUp.Classify(message, sameChatContext);
        if (route == GingerChatFollowUpKind.None) return false;
        if (_busy || _gingerRunning || _gingerDialogOpen || _agentsDialogOpen ||
            ApprovalCard.Visibility == Visibility.Visible)
        {
            ReplyText.Text = "Сначала заверши текущее действие или закрой окно агента.";
            return true;
        }

        StopVoiceListening(updateUi: false);
        _chatCancellation?.Dispose();
        using var cancellation = new CancellationTokenSource();
        _chatCancellation = cancellation;
        var generation = Interlocked.Increment(ref _chatGeneration);
        string? chatId = null;
        string? reply = null;
        var resume = false;
        var statusResolved = false;
        var hasTaskResult = false;
        SetBusy(true, canStop: true);
        try
        {
            chatId = (await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString();
            cancellation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(chatId) || chatId != selectedChat)
                throw new OperationCanceledException("Чат изменился; продолжение не применяется.");
            if (route == GingerChatFollowUpKind.Resume)
            {
                // Recheck the context after the asynchronous bridge reply. The
                // original message is passed unchanged to a fresh preparation.
                if (!sameChatContext || _gingerFollowupChatId != chatId)
                    throw new OperationCanceledException("Контекст задачи изменился.");
                if (_gingerFollowupHasTask) resume = true;
                else reply = "В этом чате ещё нет результата задачи Ginger. " +
                    "Чтобы начать новый разбор, напиши «продолжим сопоставление». Работа не запускалась.";
            }
            else if (route == GingerChatFollowUpKind.Clarify)
                reply = "Для нового разбора Ginger напиши «продолжим сопоставление». " +
                    "Начну со свежего чтения; прежние действия и подтверждения не повторяются.";
            else
            {
                var result = _gingerTaskResults.TryGetValue(chatId, out var memory) ? memory
                    : await Task.Run(() => new GingerChatTaskStatusStore().Read(chatId), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                reply = GingerChatTaskStatusFormatter.Format(result);
                statusResolved = true;
                hasTaskResult = result is not null;
                if (_gingerTaskResultSaveFailures.Contains(chatId))
                    reply += "\nПоследний результат доступен в этом запуске, но сохранить его на диск не удалось.";
            }
            var currentChat = (await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString();
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _chatGeneration) || selectedChat != _selectedChatId || chatId != currentChat)
                throw new OperationCanceledException("Чат изменился; результат не применяется.");
        }
        catch (OperationCanceledException)
        {
            resume = false;
            statusResolved = false;
            ClearGingerFollowUp();
            reply = "Запрос остановлен. Новая работа с Ginger не запускалась.";
        }
        catch (Exception ex)
        {
            resume = false;
            statusResolved = false;
            ClearGingerFollowUp();
            reply = "Не удалось прочитать достоверный результат Ginger. Новая проверка не запускалась.";
            StartupDiagnostics.Log("Ginger task status failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_chatCancellation, cancellation)) _chatCancellation = null;
            SetBusy(false);
        }
        if (selectedChat != _selectedChatId || generation != Volatile.Read(ref _chatGeneration)) return true;
        if (resume)
        {
            await RunGingerFromChatAsync(message, expectedChatId: chatId);
            return true;
        }
        if (reply is null) return true;
        if (statusResolved && chatId is not null && !cancellation.IsCancellationRequested)
        {
            _gingerFollowupChatId = chatId;
            _gingerFollowupHasTask = hasTaskResult;
        }
        AppendConversation("Вы", message);
        AppendConversation("UNIX", reply);
        MessageBox.Text = string.Empty;
        ReplyText.Text = reply;
        SetBusy(true);
        try
        {
            if (chatId is not null) await _bridge.RecordGingerChatAsync(chatId, message, reply);
        }
        catch (Exception ex) { StartupDiagnostics.Log("Ginger task status history failed", ex); }
        finally { SetBusy(false); }
        return true;
    }
}
