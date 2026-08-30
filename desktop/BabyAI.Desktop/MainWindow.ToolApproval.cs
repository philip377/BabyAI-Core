using Microsoft.UI.Xaml;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private void Root_Loaded_WithToolApproval(object sender, RoutedEventArgs e)
    {
        Root_Loaded(sender, e);
        InitializeToolApprovalControls();
    }

    private void InitializeToolApprovalControls()
    {
        ApproveButton.Click -= ApproveButton_Click;
        RejectButton.Click -= RejectButton_Click;
        ApproveButton.Click -= ApprovalApproveButton_Click;
        RejectButton.Click -= ApprovalRejectButton_Click;
        ApproveButton.Click += ApprovalApproveButton_Click;
        RejectButton.Click += ApprovalRejectButton_Click;
        ApproveButton.Content = "Разрешить один раз";
        RejectButton.Content = "Отклонить";
    }

    private async void ApprovalApproveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        int? activityTurnIndex = null;
        try
        {
            _chatCancellation?.Dispose();
            _chatCancellation = new CancellationTokenSource();
            SetBusy(true, canStop: true);

            if (TryGetToolApprovalActivity(out var activity))
            {
                // Give immediate visible feedback before the approved tool and the
                // second model pass finish. Previously the approval card stayed on
                // screen for the entire native generation, which looked like a dead
                // button even though the worker was already executing the request.
                ApprovalCard.Visibility = Visibility.Collapsed;
                ApplyState(OrbState.Executing);
                CoreStatusText.Text = "Core: агент выполняет";
                ReplyText.Text = activity;
                activityTurnIndex = CreateConversationTurn("BabyAI", activity);
            }
            else
            {
                ApplyState(OrbState.Approval);
                ReplyText.Text = "Применяю ваше решение…";
            }

            try
            {
                var reply = await _bridge.ApproveToolAsync(_chatCancellation.Token);
                ReplyText.Text = "Действие выполнено.";
                if (!string.IsNullOrWhiteSpace(reply))
                {
                    if (activityTurnIndex is int index)
                    {
                        ReplaceConversationTurn(index, "BabyAI", reply);
                        activityTurnIndex = null;
                    }
                    else
                    {
                        AppendConversation("BabyAI", reply);
                    }
                }
            }
            catch (InvalidOperationException ex) when (IsNoPendingToolApproval(ex))
            {
                if (activityTurnIndex is int index)
                {
                    RemoveConversationTurn(index);
                    activityTurnIndex = null;
                }
                await _bridge.ApproveLessonAsync();
                ReplyText.Text = "Урок подтверждён и сохранён.";
            }
            await RefreshStatusAsync();
            ApplyState(OrbState.Done);
        }
        catch (OperationCanceledException)
        {
            if (activityTurnIndex is int index)
                RemoveConversationTurn(index);
            ReplyText.Text = "Действие отменено.";
            AppendConversation("Система", "Остановлено пользователем.");
            ApplyState(OrbState.Idle);
        }
        catch (Exception ex)
        {
            if (activityTurnIndex is int index)
                RemoveConversationTurn(index);
            ShowBridgeError(ex);
        }
        finally
        {
            _chatCancellation?.Dispose();
            _chatCancellation = null;
            SetBusy(false);
            if (_expanded)
                MessageBox.Focus(FocusState.Programmatic);
        }
    }

    private async void ApprovalRejectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        try
        {
            SetBusy(true);
            ApplyState(OrbState.Approval);
            ReplyText.Text = "Отклоняю запрос…";
            try
            {
                var reply = await _bridge.RejectToolAsync();
                ReplyText.Text = "Доступ не предоставлен.";
                if (!string.IsNullOrWhiteSpace(reply))
                    AppendConversation("BabyAI", reply);
            }
            catch (InvalidOperationException ex) when (IsNoPendingToolApproval(ex))
            {
                await _bridge.RejectLessonAsync();
                ReplyText.Text = "Урок отклонён.";
            }
            await RefreshStatusAsync();
            ApplyState(OrbState.Done);
        }
        catch (Exception ex)
        {
            ShowBridgeError(ex);
        }
        finally
        {
            SetBusy(false);
            if (_expanded)
                MessageBox.Focus(FocusState.Programmatic);
        }
    }

    private bool TryGetToolApprovalActivity(out string activity)
    {
        var prompt = ApprovalDescriptionText.Text?.Trim() ?? string.Empty;
        activity = string.Empty;
        if (prompt.Length == 0
            || prompt.Contains("Сохранить предложенный урок", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (prompt.Contains("список файлов", StringComparison.OrdinalIgnoreCase))
        {
            activity = prompt.Contains("Desktop", StringComparison.OrdinalIgnoreCase)
                || prompt.Contains("рабоч", StringComparison.OrdinalIgnoreCase)
                    ? "Проверяю рабочий стол…"
                    : "Проверяю папку…";
            return true;
        }
        if (prompt.Contains("прочитать файл", StringComparison.OrdinalIgnoreCase))
            activity = "Читаю файл…";
        else if (prompt.Contains("сведения", StringComparison.OrdinalIgnoreCase))
            activity = "Проверяю сведения о компьютере…";
        else if (prompt.Contains("процесс", StringComparison.OrdinalIgnoreCase))
            activity = "Смотрю запущенные процессы…";
        else if (prompt.Contains("открыть приложение", StringComparison.OrdinalIgnoreCase))
            activity = "Открываю приложение…";
        else if (prompt.Contains("диагностическую команду", StringComparison.OrdinalIgnoreCase))
            activity = "Выполняю диагностику…";
        else if (prompt.Contains("открытых окон", StringComparison.OrdinalIgnoreCase))
            activity = "Смотрю открытые окна…";
        else if (prompt.Contains("активировать окно", StringComparison.OrdinalIgnoreCase))
            activity = "Переключаюсь на окно…";
        else if (prompt.Contains("заблокировать", StringComparison.OrdinalIgnoreCase))
            activity = "Блокирую рабочую станцию…";
        else if (prompt.Contains("снимок", StringComparison.OrdinalIgnoreCase))
            activity = "Делаю снимок экрана…";
        else
            activity = "Выполняю разрешённое действие…";

        return true;
    }

    private static bool IsNoPendingToolApproval(InvalidOperationException exception) =>
        exception.Message.Contains("No pending tool approval", StringComparison.OrdinalIgnoreCase);
}
