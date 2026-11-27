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

    private async Task RunGingerFromChatAsync(string message, string? expectedChatId = null)
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
        SetBusy(true, canStop: true);
        AppendConversation("Вы", message);
        MessageBox.Text = string.Empty;
        var transcript = new StringBuilder();
        var turn = CreateConversationTurn("UNIX", "Передаю задачу агенту Ginger…");
        GingerDesktopSession? session = null;
        GingerDesktopObservation? observation = null;
        string? proposedCandidate = null;
        GingerChatAnswerReview? review = null;
        string? chatId = null;
        static GingerChatItem Item(GingerDesktopObservation value) => new(
            value.Supplier, value.Document, value.Position, value.Title, value.Linked, value.Candidates);
        void Report(GingerChatStage stage, string text)
        {
            transcript.AppendLine(text);
            ReplaceConversationTurn(turn, "UNIX", transcript.ToString());
            session?.Record(text);
            SaveGingerLogLine(text);
            var active = stage is GingerChatStage.Started or GingerChatStage.Reading
                or GingerChatStage.Filtering or GingerChatStage.Comparing;
            ShowGingerChatStep(text, active);
            SetGingerPhase(stage switch
            {
                GingerChatStage.Cancelled => GingerCompanionPhase.Cancelled,
                GingerChatStage.Failed => GingerCompanionPhase.Failed,
                GingerChatStage.Review or GingerChatStage.Empty => GingerCompanionPhase.Reviewing,
                GingerChatStage.Comparing => GingerCompanionPhase.Checking,
                _ => GingerCompanionPhase.Reading
            });
            ApplyState(stage switch
            {
                GingerChatStage.Failed => OrbState.Error,
                GingerChatStage.Cancelled => OrbState.Idle,
                GingerChatStage.Review or GingerChatStage.Empty => OrbState.Done,
                GingerChatStage.Comparing => OrbState.Thinking,
                _ => OrbState.Executing
            });
        }
        try
        {
            chatId = (await _bridge.NavigationAsync("chat.current")).GetProperty("active_chat_id").GetString();
            cancellation.Token.ThrowIfCancellationRequested();
            if (expectedChatId is not null && chatId != expectedChatId)
                throw new OperationCanceledException("Чат изменился после разбора задания.");
            session = new GingerDesktopSession();
            var flow = new GingerChatFlow(
                async token =>
                {
                    SetGingerCaptureMode(true);
                    observation = await session.ObserveAsync(token, prepare: true);
                    return Item(observation);
                },
                async token =>
                {
                    observation = await session.PrepareFiltersAsync(observation!, token);
                    return Item(observation);
                },
                async (item, token) =>
                {
                    SetGingerCaptureMode(false);
                    var saved = new GingerLearning().ReadLanguageLesson(item.Supplier);
                    var lesson = saved is { Enabled: true } ? saved.Text : "";
                    var result = await _bridge.PreviewGingerLessonAsync(item.Supplier, item.Title,
                        lesson, item.Candidates, token);
                    token.ThrowIfCancellationRequested();
                    proposedCandidate = result.GetProperty("candidate").GetString();
                    review = new GingerChatAnswerReview(chatId!, JsonSerializer.SerializeToElement(new {
                        request = new { supplier = item.Supplier, source = item.Title, lesson, candidates = item.Candidates }, result
                    }));
                    return FormatGingerChatProposal(result);
                }, Report);
            var proposal = await flow.RunAsync(cancellation.Token);
            transcript.AppendLine().AppendLine(proposal);
            if (review is not null)
            {
                ShowGingerChatReview(review);
                ShowGingerChatLesson(new GingerChatLessonReview(chatId!, Item(observation!), new GingerLearning()));
            }
            if (observation is not null && proposedCandidate is not null && chatId is not null)
                transcript.AppendLine(OfferGingerChatMatch(session, observation, chatId, proposedCandidate));
            if (review is not null)
                transcript.AppendLine("Здесь можно оценить ответ или объяснить правило поставщика. Перед сохранением урока проверь понимание модели в блоке «Объяснить UNIX».");
            ReplaceConversationTurn(turn, "UNIX", transcript.ToString());
            session.Record(proposal);
        }
        catch (OperationCanceledException)
        {
            InvalidateGingerChatContext();
            if (!transcript.ToString().Contains("Остановлено.", StringComparison.Ordinal))
                Report(GingerChatStage.Cancelled, "Остановлено. Агент не продолжает работу.");
        }
        catch (Exception ex)
        {
            InvalidateGingerChatContext();
            // Also handles failure to acquire the session, before the flow starts.
            if (!transcript.ToString().Contains("Процесс остановлен:", StringComparison.Ordinal))
                Report(GingerChatStage.Failed, "Не удалось запустить агента Ginger: " + ex.Message);
            StartupDiagnostics.Log("Ginger chat task failed", ex);
        }
        finally
        {
            _gingerStepAnimation?.Stop();
            _gingerStepAnimation = null;
            GingerChatActivity.Visibility = Visibility.Collapsed;
            SetGingerCaptureMode(false);
            session?.Dispose();
            _gingerRunning = false;
            _gingerCancellation = _chatCancellation = null;
            try
            {
                if (chatId is not null)
                    await _bridge.RecordGingerChatAsync(chatId, message, transcript.ToString());
            }
            catch (Exception ex)
            {
                AppendConversation("Система", "Результат показан, но не записан в историю чата. " + ex.Message);
                StartupDiagnostics.Log("Ginger chat history failed", ex);
            }
            SetBusy(false);
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
