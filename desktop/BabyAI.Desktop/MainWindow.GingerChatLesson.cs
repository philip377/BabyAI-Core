using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private GingerChatLessonReview? _gingerChatLesson;
    private bool _updatingGingerLessons;

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
        GingerChatLessonSource.Text = $"Поставщик: {lesson.Supplier}\n" +
            (lesson.SourceOnly ? "Сохранённое название: " : "Пример из прайса: ") + lesson.Source;
        PopulateGingerChatLessons();
        GingerChatLessonText.Text = "";
        GingerChatLessonPreview.Text = "";
        GingerChatLessonStatus.Text = lesson.SourceOnly
            ? "Кандидатов в сохранённой позиции нет. Объясни название и проверь понимание перед сохранением урока."
            : "Добавь объяснение или выбери сохранённое. Проверь понимание на показанном примере перед сохранением.";
        GingerChatLessonCard.IsExpanded = lesson.SourceOnly;
        GingerChatLessonCard.Visibility = Visibility.Visible;
        UpdateGingerChatSavedLesson();
        UpdateGingerChatLessonControls();
    }

    private void UpdateGingerChatSavedLesson()
    {
        var activeCount = _gingerChatLesson?.Lessons.Count(lesson => lesson.Enabled) ?? 0;
        GingerChatSavedLesson.Text = _gingerChatLesson?.Saved is { } saved
            ? $"Действующих объяснений: {activeCount}.\n" + (saved.Enabled ? "Выбран действующий урок: " : "Выбран отозванный урок: ") + saved.Text + "\nПонимание при проверке: " + saved.Understanding
            : $"Действующих объяснений: {activeCount}. Новое объяснение будет добавлено отдельно; прежние сохранятся.";
    }

    private void PopulateGingerChatLessons()
    {
        _updatingGingerLessons = true;
        try
        {
            GingerChatLessonSelector.Items.Clear();
            GingerChatLessonSelector.Items.Add(new ComboBoxItem { Content = "Новое объяснение", Tag = null });
            if (_gingerChatLesson is not { } review) return;
            foreach (var lesson in review.Lessons)
            {
                var title = lesson.Text.Replace('\r', ' ').Replace('\n', ' ');
                if (title.Length > 100) title = title[..100] + "…";
                GingerChatLessonSelector.Items.Add(new ComboBoxItem {
                    Content = (lesson.Enabled ? "Действует: " : "Отозван: ") + title, Tag = lesson.LessonId
                });
            }
            GingerChatLessonSelector.SelectedIndex = 0;
            foreach (ComboBoxItem item in GingerChatLessonSelector.Items)
                if (item.Tag is string id && id == review.SelectedLessonId)
                    GingerChatLessonSelector.SelectedItem = item;
        }
        finally { _updatingGingerLessons = false; }
    }

    private void GingerChatLessonSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingGingerLessons || _gingerChatLesson is not { } lesson
            || GingerChatLessonSelector.SelectedItem is not ComboBoxItem item) return;
        try
        {
            lesson.SelectLesson(item.Tag as string);
            GingerChatLessonText.Text = lesson.Saved?.Text ?? "";
            GingerChatLessonPreview.Text = "Проверь понимание вместе с остальными действующими объяснениями.";
            GingerChatLessonStatus.Text = lesson.Saved is null ? "Новое объяснение." : "Редактируется только выбранный урок.";
            UpdateGingerChatSavedLesson();
        }
        catch (Exception ex) { GingerChatLessonStatus.Text = ex.Message; PopulateGingerChatLessons(); }
        UpdateGingerChatLessonControls();
    }

    private void UpdateGingerChatLessonControls()
    {
        GingerChatLessonText.IsEnabled = !_busy && _gingerChatLesson?.CanPreview == true;
        GingerChatLessonSelector.IsEnabled = GingerChatLessonText.IsEnabled;
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
        var notificationKind = GingerNotificationKind.Failed;
        try
        {
            var chatId = (await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString() ?? "";
            var report = await lesson.PreviewAsync(chatId, text,
                lesson.SourceOnly ? _bridge.PreviewGingerSourceLessonAsync : _bridge.PreviewGingerLessonAsync,
                cancellation.Token);
            if (!IsCurrentGingerChatLesson(lesson)) throw new OperationCanceledException();
            result = "Понимание готово к проверке.\nПоставщик: " + lesson.Supplier +
                "\nТвоё объяснение: " + text + "\nПонял так: " + lesson.Draft!.Understanding + "\nПример: " + lesson.Source + "\n" +
                (lesson.SourceOnly
                    ? "Разбор сохранённого названия: " + report.GetProperty("result").GetProperty("explanation").GetString() +
                      "\nКандидаты не проверялись. Пара для сопоставления не выбрана."
                    : FormatGingerChatProposal(report.GetProperty("result"))) +
                "\nВсе объяснения, использованные при проверке:\n" + report.GetProperty("request").GetProperty("lesson").GetString() +
                "\nПроверь понимание. Если всё верно, отдельно сохрани урок." +
                (lesson.SourceOnly
                    ? " Он будет учтён при следующих сравнениях этого поставщика. Позиция остаётся в отложенных до новой проверки в Ginger."
                    : " Для сопоставления пары снова попроси продолжить сопоставление.");
            if (!lesson.SourceOnly) ShowGingerChatReview(new GingerChatAnswerReview(lesson.ChatId, report));
            notificationKind = GingerNotificationKind.LessonReady;
        }
        catch (OperationCanceledException)
        {
            notificationKind = GingerNotificationKind.Cancelled;
            lesson.DiscardDraft(); result = "Разбор объяснения остановлен. Урок не сохранён.";
        }
        catch (Exception ex)
        {
            lesson.DiscardDraft();
            result = "Не удалось проверить объяснение: " + ex.Message;
            StartupDiagnostics.Log("Ginger chat lesson preview failed", ex);
        }
        finally
        {
            _chatCancellation = null;
            try { if (IsCurrentGingerChatLesson(lesson)) GingerChatLessonPreview.Text = result; }
            catch (Exception ex) { StartupDiagnostics.Log("Displaying Ginger lesson preview failed", ex); }
            await FinishGingerChatLessonAsync(lesson, message, result);
            NotifyGingerOutcome(notificationKind, lesson.ChatId);
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
        var editingExisting = lesson.Saved is not null;
        var message = revoke ? "Отзываю сохранённый урок поставщика." : "Подтверждаю понимание и сохраняю урок поставщика.";
        var result = "";
        try
        {
            var chatId = (await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString() ?? "";
            if (revoke) lesson.Revoke(chatId);
            else lesson.Save(chatId, GingerChatLessonText.Text);
            result = revoke
                ? "Выбранный урок отозван. Остальные действующие объяснения сохраняются."
                : (editingExisting ? "Выбранное объяснение обновлено." : "Новое объяснение добавлено отдельно.") +
                  " UNIX использует все действующие объяснения поставщика вместе.";
            result += "\nПоставщик: " + lesson.Supplier + "\nУрок: " + lesson.Saved!.Text +
                "\nПонимание: " + lesson.Saved.Understanding +
                "\nЭто сохранённое знание для запросов к Qwen. Веса модели, отдельные точные пары и разрешения Ginger не изменены.";
            PopulateGingerChatLessons();
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
        try
        {
            try
            {
                if (IsCurrentGingerChatLesson(lesson))
                {
                    var summary = result.Split('\n')[0];
                    ShowGingerChatStep(summary, false);
                    GingerChatLessonStatus.Text = summary;
                    AppendConversation("Вы", message);
                    AppendConversation("UNIX", result);
                }
            }
            catch (Exception ex) { StartupDiagnostics.Log("Displaying Ginger lesson result failed", ex); }
            try { await _bridge.RecordGingerChatAsync(lesson.ChatId, message, result); }
            catch (Exception ex)
            {
                StartupDiagnostics.Log("Ginger lesson history failed", ex);
                try
                {
                    if (IsCurrentGingerChatLesson(lesson))
                        AppendConversation("Система", "Результат не записан в историю чата. " + ex.Message);
                }
                catch (Exception displayError) { StartupDiagnostics.Log("Displaying Ginger lesson history error failed", displayError); }
            }
        }
        finally
        {
            _busy = false;
            try { SetBusy(false); }
            catch (Exception ex) { StartupDiagnostics.Log("Restoring controls after Ginger lesson failed", ex); }
        }
    }

    private bool IsCurrentGingerChatLesson(GingerChatLessonReview lesson) =>
        !_exitRequested && ReferenceEquals(_gingerChatLesson, lesson) && _selectedChatId == lesson.ChatId;
}
