using Microsoft.UI.Xaml;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private GingerChatAnswerReview? _gingerChatReview;

    private void InvalidateGingerChatContext()
    {
        InvalidateGingerChatMatch();
        _gingerChatReview?.Invalidate();
        _gingerChatReview = null;
        GingerChatReviewCard.Visibility = Visibility.Collapsed;
    }

    private void ShowGingerChatReview(GingerChatAnswerReview review)
    {
        _gingerChatReview?.Invalidate();
        _gingerChatReview = review;
        GingerChatReviewSource.Text = $"Ответ для поставщика {review.Supplier}\n{review.Source}";
        GingerChatScore.ItemsSource = Enumerable.Range(0, 11).ToArray();
        GingerChatScore.SelectedIndex = -1;
        GingerChatFeedback.Text = "";
        GingerChatRatingStatus.Text = "Оцени весь показанный ответ и объясни, что верно или что надо исправить.";
        GingerChatReviewCard.IsExpanded = false;
        GingerChatReviewCard.Visibility = Visibility.Visible;
        UpdateGingerChatReviewControls();
    }

    private void UpdateGingerChatReviewControls()
    {
        GingerChatRateButton.IsEnabled = !_busy && _gingerChatReview?.CanRate == true;
        GingerChatRevokeRatingButton.IsEnabled = !_busy && _gingerChatReview?.CanRevoke == true;
        GingerChatScore.IsEnabled = GingerChatRateButton.IsEnabled;
        GingerChatFeedback.IsEnabled = GingerChatRateButton.IsEnabled || GingerChatRevokeRatingButton.IsEnabled;
    }

    private async void GingerChatRate_Click(object sender, RoutedEventArgs e) => await SaveGingerChatReviewAsync(revoke: false);
    private async void GingerChatRevokeRating_Click(object sender, RoutedEventArgs e) => await SaveGingerChatReviewAsync(revoke: true);

    private async Task SaveGingerChatReviewAsync(bool revoke)
    {
        if (_busy || _gingerRunning || _agentsDialogOpen || _gingerDialogOpen || _gingerChatReview is not { } review) return;
        var points = GingerChatScore.SelectedItem as int?;
        var feedback = GingerChatFeedback.Text.Trim();
        if (feedback.Length == 0 || (!revoke && points is null))
        {
            GingerChatRatingStatus.Text = revoke ? "Напиши причину отзыва оценки." : "Выбери оценку 0–10 и напиши пояснение.";
            return;
        }
        StopVoiceListening(updateUi: false);
        SetBusy(true);
        var message = revoke ? "Отзываю оценку показанного ответа Ginger." : $"Оцениваю показанный ответ Ginger: {points}/10.";
        var result = "";
        ShowGingerChatStep(revoke ? "Отзываю оценку ответа…" : "Сохраняю оценку ответа…", true);
        try
        {
            var chatId = (await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString() ?? "";
            if (revoke)
            {
                await review.RevokeAsync(chatId, feedback, _bridge.RevokeGingerAnswerAsync);
                result = "Оценка отозвана и исключена из действующих оценок. Исходный ответ сохранён.";
            }
            else
            {
                var receipt = await review.RateAsync(chatId, points, feedback, _bridge.RateGingerAnswerReviewAsync);
                result = receipt.Active
                    ? $"Оценка ответа {points}/10 сохранена вместе с полным запросом, ответом и твоим пояснением."
                    : "Эта оценка была отозвана раньше и остаётся исключённой. Повторное сохранение её не восстановило.";
            }
            result += "\nПоставщик: " + review.Supplier + "\nТовар: " + review.Source +
                "\nТвоё пояснение: " + feedback +
                "\nОценка не подтверждает автоматически каждый признак товара, не даёт разрешений Ginger и не меняет веса Qwen. Это отзыв; для урока нужна отдельная проверка понимания.";
        }
        catch (Exception ex)
        {
            result = (revoke ? "Отзыв оценки не подтверждён: " : "Сохранение оценки не подтверждено: ") + ex.Message;
            StartupDiagnostics.Log("Ginger chat rating failed", ex);
        }
        finally
        {
            var summary = result.Split('\n')[0];
            ShowGingerChatStep(summary, false);
            GingerChatRatingStatus.Text = summary;
            AppendConversation("Вы", message);
            AppendConversation("UNIX", result);
            try { await _bridge.RecordGingerChatAsync(review.ChatId, message, result); }
            catch (Exception ex) { AppendConversation("Система", "Результат оценки показан, но не записан в историю чата. " + ex.Message); }
            SetBusy(false);
        }
    }
}
