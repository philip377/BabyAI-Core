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

    private async Task RunGingerFromChatAsync(string message)
    {
        if (_busy || _gingerRunning || _gingerDialogOpen || _agentsDialogOpen) return;
        if (ApprovalCard.Visibility == Visibility.Visible)
        {
            ReplyText.Text = "Сначала подтверди или отклони ожидающее действие.";
            return;
        }
        InvalidateGingerChatMatch();
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
                    return FormatGingerChatProposal(result);
                }, Report);
            var proposal = await flow.RunAsync(cancellation.Token);
            transcript.AppendLine().AppendLine(proposal);
            if (observation is not null && proposedCandidate is not null && chatId is not null)
                transcript.AppendLine(OfferGingerChatMatch(session, observation, chatId, proposedCandidate));
            transcript.AppendLine("Объяснения и оценки пока сохраняются в окне агента Ginger. Предложение модели само по себе не разрешает сопоставление.");
            ReplaceConversationTurn(turn, "UNIX", transcript.ToString());
            session.Record(proposal);
        }
        catch (OperationCanceledException)
        {
            InvalidateGingerChatMatch();
            if (!transcript.ToString().Contains("Остановлено.", StringComparison.Ordinal))
                Report(GingerChatStage.Cancelled, "Остановлено. Агент не продолжает работу.");
        }
        catch (Exception ex)
        {
            InvalidateGingerChatMatch();
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
        return text.ToString().TrimEnd();
    }
}
