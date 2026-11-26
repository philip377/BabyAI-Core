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
        InvalidateGingerChatMatch();
        StopVoiceListening(updateUi: false);
        const string message = "Подтверждаю показанную пару Ginger.";
        var transcript = new StringBuilder($"{pending.Source.Supplier} · документ {pending.Source.Document} · строка {pending.Source.Position}\n" +
            $"У поставщика: {pending.Source.Title}\nНаш товар: {pending.Gate.Candidate}\n");
        _chatCancellation?.Dispose();
        using var cancellation = new CancellationTokenSource();
        _chatCancellation = _gingerCancellation = cancellation;
        _gingerRunning = true;
        SetBusy(true, canStop: true);
        AppendConversation("Вы", message);
        var turn = CreateConversationTurn("UNIX", transcript.ToString());
        GingerDesktopSession? session = null;
        OverlappedPresenter? minimized = null;
        void Report(string text, bool active)
        {
            transcript.AppendLine(text);
            ReplaceConversationTurn(turn, "UNIX", transcript.ToString());
            session?.Record(text);
            SaveGingerLogLine(text);
            ShowGingerChatStep(text, active);
        }
        try
        {
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
                    Report(text, !verified);
                    SetGingerPhase(verified ? GingerCompanionPhase.Completed : GingerCompanionPhase.Checking);
                    ApplyState(verified ? OrbState.Done : OrbState.Executing);
                }, cancellation.Token);
            Report("Разрешение использовано. Следующее задание начнётся с нового чтения; урок и оценка автоматически не сохранялись.", false);
        }
        catch (OperationCanceledException)
        {
            Report("Остановлено. Эта попытка не повторяется; продолжение требует нового чтения Ginger.", false);
            SetGingerPhase(GingerCompanionPhase.Cancelled);
            ApplyState(OrbState.Idle);
        }
        catch (Exception ex)
        {
            Report("Сопоставление остановлено: " + ex.Message + " Автоматического повтора нет.", false);
            SetGingerPhase(GingerCompanionPhase.Failed);
            ApplyState(OrbState.Error);
            StartupDiagnostics.Log("Ginger chat match failed", ex);
        }
        finally
        {
            _gingerStepAnimation?.Stop();
            _gingerStepAnimation = null;
            GingerChatActivity.Visibility = Visibility.Collapsed;
            _gingerChatReconciling = false;
            minimized?.Restore(false);
            SetGingerCaptureMode(false);
            session?.Dispose();
            _gingerRunning = false;
            _gingerCancellation = _chatCancellation = null;
            try { await _bridge.RecordGingerChatAsync(pending.ChatId, message, transcript.ToString()); }
            catch (Exception ex)
            {
                AppendConversation("Система", "Результат показан, но не записан в историю чата. " + ex.Message);
                StartupDiagnostics.Log("Ginger chat match history failed", ex);
            }
            SetBusy(false);
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
