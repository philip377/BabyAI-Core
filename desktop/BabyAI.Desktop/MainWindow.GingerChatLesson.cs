using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private GingerChatLessonReview? _gingerChatLesson;

    private void InvalidateGingerChatLesson()
    {
        _gingerChatLesson?.Invalidate();
        _gingerChatLesson = null;
        GingerChatLessonCard.Visibility = Visibility.Collapsed;
    }

    private void ShowGingerChatLesson(GingerChatLessonReview lesson)
    {
        _gingerChatLesson?.Invalidate();
        _gingerChatLesson = lesson;
        GingerChatLessonSource.Text = $"Поставщик: {lesson.Supplier}\nПример из прайса: {lesson.Source}";
        GingerChatLessonText.Text = lesson.Saved is { Enabled: true } saved ? saved.Text : "";
        GingerChatLessonPreview.Text = "";
        GingerChatLessonStatus.Text = "Напиши объяснение и проверь, как UNIX его понял на показанном примере.";
        GingerChatLessonCard.IsExpanded = false;
        GingerChatLessonCard.Visibility = Visibility.Visible;
        UpdateGingerChatSavedLesson();
        UpdateGingerChatLessonControls();
    }

    private void UpdateGingerChatSavedLesson()
    {
        GingerChatSavedLesson.Text = _gingerChatLesson?.Saved is { } saved
            ? (saved.Enabled ? "Действующий урок: " : "Отозванный урок: ") + saved.Text + "\nПроверенное понимание: " + saved.Understanding
            : "Сохранённого урока пока нет.";
    }

    private void UpdateGingerChatLessonControls()
    {
        GingerChatLessonText.IsEnabled = !_busy && _gingerChatLesson?.CanPreview == true;
        GingerChatPreviewLessonButton.IsEnabled = GingerChatLessonText.IsEnabled;
        GingerChatSaveLessonButton.IsEnabled = !_busy && _gingerChatLesson?.CanSave == true;
        GingerChatRevokeLessonButton.IsEnabled = !_busy && _gingerChatLesson?.CanRevoke == true;
    }

    private void GingerChatLessonText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_gingerChatLesson is null) return;
        _gingerChatLesson.DiscardDraft();
        GingerChatLessonPreview.Text = "Объяснение изменено. Снова проверь понимание перед сохранением.";
        UpdateGingerChatLessonControls();
    }

    private async void GingerChatPreviewLesson_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _gingerRunning || _agentsDialogOpen || _gingerDialogOpen || _gingerChatLesson is not { } lesson) return;
        var text = GingerChatLessonText.Text.Trim();
        if (text.Length == 0) { GingerChatLessonStatus.Text = "Сначала напиши объяснение."; return; }
        InvalidateGingerChatMatch();
        InvalidateGingerChatReview();
        StopVoiceListening(updateUi: false);
        using var cancellation = new CancellationTokenSource();
        _chatCancellation = cancellation;
        SetBusy(true, canStop: true);
        ShowGingerChatStep("Проверяю понимание объяснения…", true);
        GingerChatLessonPreview.Text = "Разбираю объяснение на показанном примере…";
        var message = "Проверяю объяснение поставщика на показанном примере Ginger.";
        var result = "";
        try
        {
            var chatId = (await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString() ?? "";
            var report = await lesson.PreviewAsync(chatId, text, _bridge.PreviewGingerLessonAsync, cancellation.Token);
            result = "Понимание готово к проверке.\nПоставщик: " + lesson.Supplier +
                "\nТвоё объяснение: " + text + "\nПонял так: " + lesson.Draft!.Understanding + "\nПример: " + lesson.Source + "\n" +
                FormatGingerChatProposal(report.GetProperty("result")) +
                "\nПроверь понимание. Если всё верно, отдельно сохрани урок. Для сопоставления пары снова попроси продолжить сопоставление.";
            ShowGingerChatReview(new GingerChatAnswerReview(lesson.ChatId, report));
        }
        catch (OperationCanceledException) { lesson.DiscardDraft(); result = "Разбор объяснения остановлен. Урок не сохранён."; }
        catch (Exception ex)
        {
            lesson.DiscardDraft();
            result = "Не удалось проверить объяснение: " + ex.Message;
            StartupDiagnostics.Log("Ginger chat lesson preview failed", ex);
        }
        finally
        {
            _chatCancellation = null;
            GingerChatLessonPreview.Text = result;
            await FinishGingerChatLessonAsync(lesson, message, result);
        }
    }

    private async void GingerChatSaveLesson_Click(object sender, RoutedEventArgs e) => await SaveGingerChatLessonAsync(revoke: false);
    private async void GingerChatRevokeLesson_Click(object sender, RoutedEventArgs e) => await SaveGingerChatLessonAsync(revoke: true);

    private async Task SaveGingerChatLessonAsync(bool revoke)
    {
        if (_busy || _gingerRunning || _agentsDialogOpen || _gingerDialogOpen || _gingerChatLesson is not { } lesson) return;
        StopVoiceListening(updateUi: false);
        InvalidateGingerChatMatch();
        SetBusy(true);
        ShowGingerChatStep(revoke ? "Отзываю урок…" : "Сохраняю подтверждённый урок…", true);
        var message = revoke ? "Отзываю сохранённый урок поставщика." : "Подтверждаю понимание и сохраняю урок поставщика.";
        var result = "";
        try
        {
            var chatId = (await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString() ?? "";
            if (revoke) lesson.Revoke(chatId);
            else lesson.Save(chatId, GingerChatLessonText.Text);
            result = revoke
                ? "Урок отозван. При следующих запросах UNIX не будет использовать его как действующее объяснение."
                : "Урок сохранён. UNIX использует его при следующих предложениях для этого поставщика.";
            result += "\nПоставщик: " + lesson.Supplier + "\nУрок: " + lesson.Saved!.Text +
                "\nПонимание: " + lesson.Saved.Understanding +
                "\nЭто сохранённое знание для запросов к Qwen. Веса модели, отдельные точные пары и разрешения Ginger не изменены.";
            UpdateGingerChatSavedLesson();
        }
        catch (Exception ex)
        {
            result = (revoke ? "Не удалось отозвать урок: " : "Не удалось сохранить урок: ") + ex.Message;
            StartupDiagnostics.Log("Ginger chat lesson storage failed", ex);
        }
        finally { await FinishGingerChatLessonAsync(lesson, message, result); }
    }

    private async Task FinishGingerChatLessonAsync(GingerChatLessonReview lesson, string message, string result)
    {
        var summary = result.Split('\n')[0];
        ShowGingerChatStep(summary, false);
        GingerChatLessonStatus.Text = summary;
        AppendConversation("Вы", message);
        AppendConversation("UNIX", result);
        try { await _bridge.RecordGingerChatAsync(lesson.ChatId, message, result); }
        catch (Exception ex) { AppendConversation("Система", "Результат показан, но не записан в историю чата. " + ex.Message); }
        finally { SetBusy(false); }
    }
}
