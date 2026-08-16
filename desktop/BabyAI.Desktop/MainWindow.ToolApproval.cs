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

        try
        {
            SetBusy(true);
            try
            {
                var reply = await _bridge.ApproveToolAsync();
                ReplyText.Text = "Действие выполнено.";
                if (!string.IsNullOrWhiteSpace(reply))
                    AppendConversation("BabyAI", reply);
            }
            catch (InvalidOperationException ex) when (IsNoPendingToolApproval(ex))
            {
                await _bridge.ApproveLessonAsync();
                ReplyText.Text = "Урок подтверждён и сохранён.";
            }
            await RefreshStatusAsync();
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

    private async void ApprovalRejectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        try
        {
            SetBusy(true);
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

    private static bool IsNoPendingToolApproval(InvalidOperationException exception) =>
        exception.Message.Contains("No pending tool approval", StringComparison.OrdinalIgnoreCase);
}
