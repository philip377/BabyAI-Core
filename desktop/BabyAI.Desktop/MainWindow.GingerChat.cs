using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private Storyboard? _gingerStepAnimation;

    private void ShowGingerChatStep(string text, bool active)
    {
        _gingerStepAnimation?.Stop();
        _gingerStepAnimation = null;
        GingerChatStageText.Text = text;
        GingerChatActivity.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ReplyText.Text = text;
        if (!active || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var animation = new Storyboard();
        var dots = new[] { GingerStepDot1, GingerStepDot2, GingerStepDot3 };
        for (var i = 0; i < dots.Length; i++)
        {
            var pulse = new DoubleAnimation
            {
                From = 0.25, To = 1, AutoReverse = true,
                Duration = new Duration(TimeSpan.FromMilliseconds(450)),
                BeginTime = TimeSpan.FromMilliseconds(i * 120),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(pulse, dots[i]);
            Storyboard.SetTargetProperty(pulse, "Opacity");
            animation.Children.Add(pulse);
        }
        _gingerStepAnimation = animation;
        animation.Begin();
    }

    private async Task<bool> TryRouteGingerFromChatAsync(string message)
    {
        if (await TryRouteGingerFollowUpAsync(message)) return true;
        if (GingerChatIntent.IsStart(message))
        {
            await RunGingerFromChatAsync(message);
            return true;
        }
        if (!GingerChatIntent.MayNeedReview(message)) return false;
        if (_gingerRunning || _gingerDialogOpen || _agentsDialogOpen ||
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
        var selectedChat = _selectedChatId;
        string? chatId = null;
        var route = GingerChatRoute.Other;
        string? reply = null;
        SetBusy(true, canStop: true);
        ApplyState(OrbState.Thinking);
        CoreStatusText.Text = "Core: понимаю задание";
        ShowGingerChatStep("Понимаю задание. Компьютером пока не управляю…", true);
        try
        {
            chatId = (await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString();
            cancellation.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(chatId))
                throw new InvalidDataException("Не удалось определить текущий чат.");
            route = await GingerChatIntent.ResolveAsync(message, _bridge.PreviewGingerIntentAsync, cancellation.Token);
            var currentChat = (await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString();
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _chatGeneration) || selectedChat != _selectedChatId || chatId != currentChat)
                throw new OperationCanceledException("Чат изменился; результат разбора не применяется.");
            if (route == GingerChatRoute.Clarify)
                reply = "Задание требует уточнения. Сейчас из чата можно подготовить кандидатов для текущей " +
                    "выделенной строки Ginger. Выбор другого товара, поставщика или количества строк ещё не поддержан. " +
                    "Работа с Ginger не запускалась.";
        }
        catch (OperationCanceledException)
        {
            reply = "Разбор задания остановлен. Ginger не запущен.";
        }
        catch (Exception ex)
        {
            reply = "Не удалось разобрать задание. Ginger не запущен. " + ex.Message;
            StartupDiagnostics.Log("Ginger chat intent failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_chatCancellation, cancellation)) _chatCancellation = null;
            ShowGingerChatStep(reply ?? "Разбор задания завершён.", false);
            CoreStatusText.Text = "Core: подключён";
            ApplyState(OrbState.Idle);
            SetBusy(false);
        }
        if (reply is not null)
        {
            ClearGingerFollowUp();
            // A failed, unclear or cancelled interpretation never falls through
            // to the general agent with a half-understood executable request.
            if (selectedChat != _selectedChatId) return true;
            AppendConversation("Вы", message);
            AppendConversation("UNIX", reply);
            MessageBox.Text = string.Empty;
            SetBusy(true);
            try
            {
                if (chatId is not null) await _bridge.RecordGingerChatAsync(chatId, message, reply);
            }
            catch (Exception ex) { StartupDiagnostics.Log("Ginger intent history failed", ex); }
            finally { SetBusy(false); }
            return true;
        }
        if (route == GingerChatRoute.Other) return false;
        await RunGingerFromChatAsync(message, expectedChatId: chatId);
        return true;
    }

    private async Task RunGingerFromChatAsync(string message, string? expectedChatId = null,
        GingerDeferredRow? revisit = null)
    {
        if (_busy || _gingerRunning || _gingerDialogOpen || _agentsDialogOpen) return;
        if (ApprovalCard.Visibility == Visibility.Visible)
        {
            ReplyText.Text = "Сначала подтверди или отклони ожидающее действие.";
            return;
        }
        InvalidateGingerChatContext();
        StopVoiceListening(updateUi: false);
        _chatCancellation?.Dispose();
        using var cancellation = new CancellationTokenSource();
        _chatCancellation = _gingerCancellation = cancellation;
        _gingerRunning = true;
        var transcript = new StringBuilder();
        var taskRunId = Guid.NewGuid().ToString("N");
        var taskStarted = DateTimeOffset.UtcNow;
        var taskState = GingerChatTaskState.Failed;
        GingerProgressTurn? progress = null;
        var summary = "Подготовка остановлена. Подробности доступны ниже.";
        string? failureStatus = null;
        var deferred = new List<GingerDeferredRow>();
        GingerDesktopSession? session = null;
        GingerDesktopObservation? observation = null;
        string? proposedCandidate = null;
        GingerChatAnswerReview? review = null;
        string? reviewObservationDirectory = null;
        string? chatId = null;
        var notificationKind = GingerNotificationKind.Failed;
        static GingerChatItem Item(GingerDesktopObservation value) => new(
            value.Supplier, value.Document, value.Position, value.Title, value.Linked, value.Candidates);
        void Report(GingerChatStage stage, string text)
        {
            transcript.AppendLine(text);
            session?.Record(text);
            SaveGingerLogLine(text);
            progress?.Report(stage, text);
            // The card owns the active step animation; do not show the legacy dots as well.
            ShowGingerChatStep(text, false);
            SetGingerPhase(stage switch
            {
                GingerChatStage.Cancelled => GingerCompanionPhase.Cancelled,
                GingerChatStage.Failed => GingerCompanionPhase.Failed,
                GingerChatStage.Completed => GingerCompanionPhase.Completed,
                GingerChatStage.Review or GingerChatStage.Empty => GingerCompanionPhase.Reviewing,
                GingerChatStage.Comparing => GingerCompanionPhase.Checking,
                _ => GingerCompanionPhase.Reading
            });
            ApplyState(stage switch
            {
                GingerChatStage.Failed => OrbState.Error,
                GingerChatStage.Cancelled => OrbState.Idle,
                GingerChatStage.Completed => OrbState.Done,
                GingerChatStage.Review or GingerChatStage.Empty => OrbState.Done,
                GingerChatStage.Comparing => OrbState.Thinking,
                _ => OrbState.Executing
            });
        }
        try
        {
            SetBusy(true, canStop: true);
            AppendConversation("Вы", message);
            MessageBox.Text = string.Empty;
            progress = CreateGingerProgressTurn();
            chatId = (await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString();
            cancellation.Token.ThrowIfCancellationRequested();
            if (expectedChatId is not null && chatId != expectedChatId)
                throw new OperationCanceledException("Чат изменился после разбора задания.");
            if (string.IsNullOrWhiteSpace(chatId))
                throw new InvalidDataException("Не удалось определить чат для сохранения отложенных строк.");
            session = new GingerDesktopSession();
            session.FilterProgress = text => Report(GingerChatStage.Filtering, text);
            static GingerChatScanRow Row(GingerDesktopObservation value) => new(Item(value), value.Count,
                value.Data.GetProperty("process_id").GetInt64(), value.Source.GetProperty("TableHandle").GetInt64(), value.Directory);
            async Task<GingerChatScanRow> Read(CancellationToken token)
            {
                SetGingerCaptureMode(true);
                observation = await ReadGingerChatStartAsync(session, revisit, token);
                return Row(observation);
            }
            async Task<GingerChatScanRow> Filter(GingerChatScanRow before, CancellationToken token)
            {
                if (observation?.Directory != before.ObservationDirectory)
                    throw new InvalidDataException("Наблюдение строки изменилось до подготовки фильтров.");
                observation = await session.PrepareFiltersAsync(observation, token);
                // Even a plan with no new stars needs a fresh read before deferring.
                if (observation.Candidates.Length == 0)
                    observation = await session.ObserveAsync(token);
                return Row(observation);
            }
            async Task<GingerChatComparison> Compare(GingerChatItem item, CancellationToken token)
            {
                review?.Invalidate();
                review = null;
                reviewObservationDirectory = null;
                proposedCandidate = null;
                var compared = observation ?? throw new InvalidDataException("Нет наблюдения для сравнения.");
                if (compared.Supplier != item.Supplier || compared.Document != item.Document ||
                    compared.Position != item.Position || compared.Title != item.Title ||
                    !compared.Candidates.SequenceEqual(item.Candidates, StringComparer.Ordinal))
                    throw new InvalidDataException("Наблюдение изменилось до сравнения кандидатов.");
                SetGingerCaptureMode(false);
                var lessons = new GingerLearning().ReadLanguageLessons(item.Supplier);
                var lesson = GingerLearning.ComposeLanguageLessons(lessons);
                var candidates = item.Candidates.ToArray();
                var result = await _bridge.PreviewGingerLessonAsync(item.Supplier, item.Title,
                    lesson, candidates, token);
                token.ThrowIfCancellationRequested();
                var report = JsonSerializer.SerializeToElement(new {
                    request = new { supplier = item.Supplier, source = item.Title, lesson, candidates }, result
                });
                var formatted = FormatGingerChatProposal(result);
                // Preserve every completed answer before a possible deferral or next-row input.
                // Validation or disk failure stops the scan; it is never treated as ambiguity.
                var reportPath = await Task.Run(() => GingerChatComparisonEvidence.Save(compared.Directory, report), token);
                token.ThrowIfCancellationRequested();
                session.Record("Полный отчёт сравнения сохранён: " + reportPath);
                proposedCandidate = result.GetProperty("candidate").GetString();
                review = new GingerChatAnswerReview(chatId!, report);
                reviewObservationDirectory = compared.Directory;
                return new(formatted, proposedCandidate);
            }
            string proposal;
            GingerChatScanResult? scan = null;
            if (revisit is not null)
            {
                // An explicit review of a saved row must stay on that row, even when still empty.
                var one = new GingerChatFlow(async token => (await Read(token)).Item,
                    async token => (await Filter(Row(observation!), token)).Item,
                    async (item, token) => (await Compare(item, token)).Proposal, Report);
                proposal = await one.RunAsync(cancellation.Token);
            }
            else
            {
                var flow = new GingerChatScanFlow(Read, Filter,
                    async (before, token) =>
                    {
                        if (observation?.Directory != before.ObservationDirectory)
                            throw new InvalidDataException("Наблюдение изменилось до перехода к следующей строке.");
                        SetGingerCaptureMode(true);
                        observation = await session.ObserveAsync(token, observation, before.Item.Position + 1,
                            prepareFiltersOnOverflow: true);
                        return Row(observation);
                    },
                    async (before, token) =>
                    {
                        var saved = await Task.Run(() => before.Item.Candidates.Length == 0
                            ? new GingerDeferredRows().Add(before.Item, chatId!, before.ObservationDirectory)
                            : new GingerDeferredRows().AddManualReview(before.Item, chatId!, before.ObservationDirectory), token);
                        deferred.Add(saved);
                        // A saved ambiguous answer belongs to the skipped row, never to the next one.
                        review?.Invalidate();
                        review = null;
                        reviewObservationDirectory = null;
                        proposedCandidate = null;
                    }, Compare, Report);
                scan = await flow.RunAsync(cancellation.Token);
                proposal = scan.Proposal is null ? "В открытом прайсе больше нет следующих строк."
                    : $"{scan.Row.Item.Supplier} · документ {scan.Row.Item.Document} · строка {scan.Row.Item.Position}\n{scan.Row.Item.Title}\n{scan.Proposal}";
            }
            if (observation is not null && chatId is not null)
                ShowBottleResearch(chatId, observation.Title);
            transcript.AppendLine().AppendLine(proposal);
            if (review is not null)
            {
                if (observation is null || reviewObservationDirectory != observation.Directory ||
                    review.Supplier != observation.Supplier || review.Source != observation.Title)
                    throw new InvalidDataException("Ответ относится к другой строке; карточки не показаны.");
                ShowGingerChatReview(review);
                ShowGingerChatLesson(new GingerChatLessonReview(chatId!, Item(observation!), new GingerLearning()));
            }
            if (observation is not null && proposedCandidate is not null && chatId is not null)
                transcript.AppendLine(OfferGingerChatMatch(session, observation, chatId, proposedCandidate));
            if (review is not null)
                transcript.AppendLine("Здесь можно оценить ответ или объяснить правило поставщика. Перед сохранением урока проверь понимание модели в блоке «Объяснить UNIX».");
            session.Record(proposal);
            if (scan?.ReachedEnd == true)
            {
                taskState = GingerChatTaskState.End;
                summary = $"Дошёл до конца открытого прайса. Отложено для разбора: {scan.DeferredCount}. " +
                    $"Уже сопоставленных строк пройдено: {scan.AlreadyLinkedCount}.";
                notificationKind = deferred.Count > 0 ? GingerNotificationKind.ManualReview : GingerNotificationKind.RunFinished;
            }
            else
            {
                taskState = _gingerChatConfirmation is not null ? GingerChatTaskState.PairReady
                    : observation?.Linked == true ? GingerChatTaskState.AlreadyLinked : GingerChatTaskState.Review;
                notificationKind = GingerTaskNotification.ForPreparation(
                    observation?.Linked == true, _gingerChatConfirmation is not null);
                summary = _gingerChatConfirmation is not null
                    ? $"Строка {observation!.Position}: пара готова к твоему подтверждению. Сопоставление ещё не выполнено."
                    : observation?.Linked == true ? "Эта строка уже сопоставлена. Повтор не выполнялся."
                    : $"Строка {observation?.Position}: нужна ручная проверка. Однозначная пара не подтверждена.";
                if (deferred.Count > 0) summary += $" Отложено для разбора: {deferred.Count}.";
            }
        }
        catch (TimeoutException ex)
        {
            taskState = GingerChatTaskState.TimedOut;
            notificationKind = GingerNotificationKind.Failed;
            summary = GingerChatOutcomeText.TimedOut(deferred.Count);
            failureStatus = "время ожидания истекло";
            InvalidateGingerChatContext();
            Report(GingerChatStage.Failed, "Время ожидания текущего шага истекло: " + ex.Message + " " +
                GingerChatOutcomeText.Deferred(deferred.Count) + " Отправленные действия не повторяются.");
            StartupDiagnostics.Log("Ginger chat step timed out", ex);
        }
        catch (OperationCanceledException)
        {
            taskState = GingerChatTaskState.Cancelled;
            notificationKind = GingerNotificationKind.Cancelled;
            summary = GingerChatOutcomeText.Cancelled(cancellation.IsCancellationRequested, deferred.Count);
            failureStatus = cancellation.IsCancellationRequested ? "остановлено пользователем" : "прервано";
            InvalidateGingerChatContext();
            Report(GingerChatStage.Cancelled, summary + " Отправленные действия не повторяются.");
        }
        catch (Exception ex)
        {
            taskState = GingerChatTaskState.Failed;
            summary = GingerChatOutcomeText.Failed(deferred.Count);
            failureStatus = "ошибка";
            InvalidateGingerChatContext();
            // Also handles failure to acquire the session, before the flow starts.
            if (!transcript.ToString().Contains("Процесс остановлен:", StringComparison.Ordinal))
                Report(GingerChatStage.Failed, "Не удалось запустить агента Ginger: " + ex.Message);
            StartupDiagnostics.Log("Ginger chat task failed", ex);
        }
        finally
        {
            RecordGingerTaskResult(chatId, taskRunId, taskStarted, taskState, deferred.Count, summary);
            try
            {
                try
                {
                    _gingerStepAnimation?.Stop();
                    _gingerStepAnimation = null;
                    GingerChatActivity.Visibility = Visibility.Collapsed;
                    SetGingerCaptureMode(false);
                }
                catch (Exception ex) { StartupDiagnostics.Log("Ginger chat display cleanup failed", ex); }
                try { session?.Dispose(); }
                catch (Exception ex) { StartupDiagnostics.Log("Ginger chat session cleanup failed", ex); }
                if (deferred.Count > 0)
                {
                    transcript.AppendLine().AppendLine("Отложенные позиции для разбора:");
                    foreach (var row in deferred)
                        transcript.AppendLine($"{row.Supplier} · документ {row.Document} · строка {row.Position}: {row.Title}\n{row.Reason}");
                    transcript.AppendLine("Список сохранён отдельно от истории чата. Открой «Отложенные позиции», чтобы вернуться к разбору.");
                }
                var details = transcript.ToString();
                if (details.Length > 20_000)
                {
                    // Full observations and per-step messages remain in the durable session journal.
                    var length = char.IsHighSurrogate(details[19_999]) ? 19_999 : 20_000;
                    details = details[..length] + $"\n\nПоказана первая часть подробностей. Полный журнал: {session?.DirectoryPath}\\journal-ru.txt. Отложенные строки доступны отдельным списком.";
                }
                var savedTranscript = summary + "\n\n" + details;
                try
                {
                    progress?.Finish(summary, details, failureStatus);
                    if (progress is not null) savedTranscript = progress.Transcript;
                }
                catch (Exception ex) { StartupDiagnostics.Log("Ginger chat final display failed", ex); }
                try { await RefreshGingerDeferredRowsAsync(); }
                catch (Exception ex) { StartupDiagnostics.Log("Ginger deferred list refresh failed", ex); }
                try
                {
                    if (chatId is not null)
                        await _bridge.RecordGingerChatAsync(chatId, message, savedTranscript);
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.Log("Ginger chat history failed", ex);
                    AppendConversation("Система", "Результат показан, но не записан в историю чата. " + ex.Message);
                }
            }
            finally
            {
                _gingerRunning = false;
                _gingerCancellation = _chatCancellation = null;
                try { SetBusy(false); }
                finally { NotifyGingerOutcome(notificationKind, chatId); }
            }
        }
    }

    private static string FormatGingerChatProposal(JsonElement result)
    {
        var text = new StringBuilder();
        text.AppendLine("Кандидат: " + (result.GetProperty("candidate").GetString() ?? "нужна ручная проверка"));
        text.AppendLine(result.GetProperty("explanation").GetString());
        if (result.TryGetProperty("comparisons", out var comparisons))
            foreach (var comparison in comparisons.EnumerateArray())
            {
                text.AppendLine(comparison.GetProperty("candidate").GetString());
                foreach (var attribute in comparison.GetProperty("attributes").EnumerateObject())
                {
                    var name = attribute.Name switch { "identity" => "Название", "volume" => "Объём",
                        "concentration" => "Концентрация", "tester" => "Тестер", _ => "Версия/комплектация" };
                    var verdict = attribute.Value.GetString() switch { "match" => "совпадает",
                        "conflict" => "различается", _ => "не определено" };
                    text.Append(name).Append(": ").Append(verdict).Append("; ");
                }
                text.AppendLine();
            }
        text.Append(FormatGingerIdentityEvidence(result));
        text.Append(FormatGingerVolumeEvidence(result));
        return text.ToString().TrimEnd();
    }

    private static string FormatGingerIdentityEvidence(JsonElement result)
    {
        if (!result.TryGetProperty("identity_evidence", out var evidence)) return "";
        static string Fields(JsonElement value) => value.ValueKind == JsonValueKind.Object
            ? $"бренд «{value.GetProperty("brand").GetString()}»; аромат «{value.GetProperty("fragrance").GetString()}»"
            : "бренд и полное название не удалось выделить";
        var text = new StringBuilder("\nРазбор названий моделью — проверь выделенные слова:\n");
        text.AppendLine("В прайсе: " + Fields(evidence.GetProperty("source_fields")));
        foreach (var item in evidence.GetProperty("candidates").EnumerateArray())
        {
            text.AppendLine(item.GetProperty("candidate").GetString());
            text.AppendLine(Fields(item.GetProperty("candidate_fields")) + ". " + item.GetProperty("reason").GetString());
        }
        return text.ToString();
    }

    private static string FormatGingerVolumeEvidence(JsonElement result)
    {
        if (!result.TryGetProperty("volume_evidence", out var evidence)) return "";
        static string Measure(JsonElement value)
        {
            var quotes = string.Join(", ", value.GetProperty("evidence").EnumerateArray()
                .Select(item => "«" + item.GetString() + "»"));
            var ml = value.GetProperty("millilitres").GetString();
            return (quotes.Length == 0 ? "Явный объём не прочитан" : quotes)
                + (ml is null ? ". " + value.GetProperty("reason").GetString() : " → " + ml + " мл");
        }
        var text = new StringBuilder("\nПроверка указанных объёмов:\n");
        text.AppendLine("В прайсе: " + Measure(evidence.GetProperty("source_measure")));
        foreach (var item in evidence.GetProperty("candidates").EnumerateArray())
        {
            text.AppendLine(item.GetProperty("candidate").GetString());
            text.AppendLine(Measure(item.GetProperty("measure")) + ". " + item.GetProperty("reason").GetString());
        }
        return text.ToString();
    }
}
