using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private sealed record GingerChatConfirmation(GingerChatMatchGate Gate, GingerDesktopObservation Source, string ChatId);
    private GingerChatConfirmation? _gingerChatConfirmation;
    private bool _gingerChatReconciling;

    private static GingerChatMatchSnapshot MatchSnapshot(GingerDesktopObservation value) => new(
        value.Data.GetProperty("process_id").GetInt64(), value.Source.GetProperty("TableHandle").GetInt64(),
        value.Count, value.Source.GetProperty("PriceText").GetString()!,
        value.Source.GetProperty("LinkedProductText").GetString()!,
        new(value.Supplier, value.Document, value.Position, value.Title, value.Linked, value.Candidates));

    private void InvalidateGingerChatMatch()
    {
        _gingerChatConfirmation = null;
        GingerChatConfirmationCard.Visibility = Visibility.Collapsed;
    }

    private string OfferGingerChatMatch(GingerDesktopSession session, GingerDesktopObservation source,
        string chatId, string candidate)
    {
        if (source.Linked || source.Candidates.Count(c => c == candidate) != 1)
            return "Однозначная пара не подтверждена. Нужна ручная проверка.";
        if (session.HasPriorAttempt(source))
            return "Есть защитная запись прежней попытки. Повтор не предлагается; нужна проверка результата.";
        var gate = new GingerChatMatchGate(MatchSnapshot(source), chatId, candidate);
        _gingerChatConfirmation = new(gate, source, chatId);
        GingerChatPairText.Text = $"{source.Supplier} · документ {source.Document} · строка {source.Position}\n" +
            $"У поставщика: {source.Title}\nНаш товар: {candidate}";
        GingerChatConfirmationCard.Visibility = Visibility.Visible;
        return "Проверь полные названия в карточке ниже. «Сопоставить эту пару» разрешает только эту одну привязку. Подтверждение действует 15 минут и не сохраняется после перезапуска.";
    }

    private async void GingerChatConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _gingerRunning || _gingerDialogOpen || _agentsDialogOpen ||
            ApprovalCard.Visibility == Visibility.Visible || _gingerChatConfirmation is not { } pending) return;
        const string message = "Подтверждаю показанную пару Ginger.";
        var transcript = new StringBuilder($"{pending.Source.Supplier} · документ {pending.Source.Document} · строка {pending.Source.Position}\n" +
            $"У поставщика: {pending.Source.Title}\nНаш товар: {pending.Gate.Candidate}\n");
        using var cancellation = new CancellationTokenSource();
        GingerProgressTurn? progress = null;
        GingerDesktopSession? session = null;
        OverlappedPresenter? minimized = null;
        var matchVerified = false;
        var matchCancelled = false;
        var matchFailed = false;
        static void BestEffort(string operation, Action action)
        {
            try { action(); }
            catch (Exception ex) { StartupDiagnostics.Log("Ginger chat match " + operation, ex); }
        }
        void Record(string text)
        {
            transcript.AppendLine(text);
            session?.Record(text);
            SaveGingerLogLine(text);
        }
        void Report(GingerChatMatchStage stage, string text)
        {
            Record(text);
            progress?.Report(stage, text);
            ReplyText.Text = text;
        }
        void ReportOutcome(GingerChatStage stage, string text)
        {
            // The outcome remains available even if presentation or logging
            // failed while reporting an earlier stage.
            transcript.AppendLine(text);
            BestEffort("outcome journal failed", () => session?.Record(text));
            BestEffort("outcome log failed", () => SaveGingerLogLine(text));
            BestEffort("outcome card failed", () => progress?.Report(stage, text));
            BestEffort("outcome text failed", () => ReplyText.Text = text);
        }
        try
        {
            InvalidateGingerChatMatch();
            StopVoiceListening(updateUi: false);
            _chatCancellation?.Dispose();
            _chatCancellation = _gingerCancellation = cancellation;
            _gingerRunning = true;
            SetBusy(true, canStop: true);
            AppendConversation("Вы", message);
            progress = CreateGingerProgressTurn();
            var activeChat = (await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString();
            cancellation.Token.ThrowIfCancellationRequested();
            session = new GingerDesktopSession();
            session.Record("Пользователь подтвердил в чате одну показанную пару. Разрешение на весь прайс и обучение не выдавалось.");
            await pending.Gate.RunAsync(activeChat ?? "",
                async token =>
                {
                    minimized = AppWindow.Presenter as OverlappedPresenter
                        ?? throw new InvalidOperationException("Не удалось убрать окно UNIX перед сопоставлением.");
                    SetGingerCaptureMode(true);
                    minimized.Minimize();
                    return MatchSnapshot(await session.ObserveAsync(token));
                },
                () => session.HasPriorAttempt(pending.Source),
                async token =>
                {
                    _gingerChatReconciling = true;
                    // Pass the original observation: the writer also checks every table row,
                    // active blue selection, query identity and its durable one-use claim.
                    return MatchSnapshot(await session.MatchAsync(pending.Source, pending.Gate.Candidate, token));
                },
                (stage, text) =>
                {
                    var verified = stage == GingerChatMatchStage.Verified;
                    if (verified) matchVerified = true;
                    Report(stage, text);
                    SetGingerPhase(verified ? GingerCompanionPhase.Completed : GingerCompanionPhase.Checking);
                    ApplyState(verified ? OrbState.Done : OrbState.Executing);
                }, cancellation.Token);
            Record("Разрешение использовано. Следующее задание начнётся с нового чтения; урок и оценка автоматически не сохранялись.");
        }
        catch (OperationCanceledException)
        {
            matchCancelled = true;
            ReportOutcome(GingerChatStage.Cancelled,
                "Остановлено. Эта попытка не повторяется; продолжение требует нового чтения Ginger.");
            BestEffort("cancelled phase failed", () => SetGingerPhase(GingerCompanionPhase.Cancelled));
            BestEffort("cancelled state failed", () => ApplyState(OrbState.Idle));
        }
        catch (Exception ex)
        {
            matchFailed = true;
            ReportOutcome(GingerChatStage.Failed,
                "Сопоставление остановлено: " + ex.Message + " Автоматического повтора нет.");
            BestEffort("failed phase failed", () => SetGingerPhase(GingerCompanionPhase.Failed));
            BestEffort("failed state failed", () => ApplyState(OrbState.Error));
            StartupDiagnostics.Log("Ginger chat match failed", ex);
        }
        finally
        {
            try
            {
                BestEffort("animation cleanup failed", () => _gingerStepAnimation?.Stop());
                BestEffort("activity cleanup failed", () => GingerChatActivity.Visibility = Visibility.Collapsed);
                BestEffort("window restore failed", () => minimized?.Restore(false));
                BestEffort("capture cleanup failed", () => SetGingerCaptureMode(false));
                BestEffort("session cleanup failed", () => session?.Dispose());

                var summary = matchVerified
                    ? matchFailed ? "Сопоставление проверено в Ginger. После проверки произошла ошибка; подробности ниже."
                        : "Сопоставление проверено в Ginger. Показанная пара связана."
                    : matchCancelled ? "Остановлено. Результат сопоставления не подтверждён; эта попытка не повторяется."
                        : "Результат сопоставления не подтверждён. Нужна проверка; автоматического повтора нет.";
                var details = transcript.ToString();
                var historyResult = summary + "\n\n" + details;
                BestEffort("final card failed", () =>
                {
                    if (progress is null) return;
                    progress.Finish(summary, details);
                    historyResult = progress.Transcript;
                });
                try { await _bridge.RecordGingerChatAsync(pending.ChatId, message, historyResult); }
                catch (Exception ex)
                {
                    BestEffort("history notice failed", () => AppendConversation("Система",
                        "Результат не записан в историю чата. " + ex.Message));
                    StartupDiagnostics.Log("Ginger chat match history failed", ex);
                }
            }
            finally
            {
                // No rendering, window restoration or history failure can
                // retain the driver/confirmation flags or the busy state.
                _gingerStepAnimation = null;
                _gingerChatReconciling = false;
                _gingerRunning = false;
                _gingerCancellation = _chatCancellation = null;
                BestEffort("busy cleanup failed", () => SetBusy(false));
                _busy = false;
                BestEffort("notification failed", () => NotifyGingerOutcome(
                    GingerTaskNotification.ForMatch(matchVerified, matchCancelled), pending.ChatId));
            }
        }
    }

    private async void GingerChatReject_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _gingerChatConfirmation is not { } pending) return;
        InvalidateGingerChatMatch();
        StopVoiceListening(updateUi: false);
        const string message = "Отклоняю показанную пару Ginger.";
        var result = $"Пара отклонена: {pending.Source.Title} → {pending.Gate.Candidate}. Сопоставление не выполнялось.";
        SetBusy(true);
        AppendConversation("Вы", message);
        AppendConversation("UNIX", result);
        try { await _bridge.RecordGingerChatAsync(pending.ChatId, message, result); }
        catch (Exception ex) { AppendConversation("Система", "Отказ не записан в историю. " + ex.Message); }
        finally { SetBusy(false); }
    }
}
