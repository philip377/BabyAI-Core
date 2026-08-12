using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace BabyAI.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly BabyAIBridgeClient _bridge = new();
    private OrbState _state = OrbState.Idle;
    private bool _expanded;

    public MainWindow()
    {
        InitializeComponent();
        Title = "BabyAI";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ConfigureWindow();
        ApplyState(OrbState.Idle);
    }

    private void ConfigureWindow()
    {
        AppWindow.Resize(new SizeInt32(132, 132));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
    }

    private async void OrbButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _expanded = !_expanded;
            if (!_expanded)
            {
                CollapsePanel();
                ApplyState(OrbState.Idle);
                return;
            }

            ExpandPanel();
            ApplyState(OrbState.Thinking);
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ReplyText.Text = ex.Message;
            ApplyState(OrbState.Error);
        }
    }

    private async Task RefreshStatusAsync()
    {
        var status = await _bridge.StatusAsync();
        IdentityText.Text = status.Name;
        TaskText.Text = string.IsNullOrWhiteSpace(status.TaskGoal) ? "No active task" : status.TaskGoal;
        ApproveButton.Visibility = status.RequiresApproval ? Visibility.Visible : Visibility.Collapsed;
        RejectButton.Visibility = status.RequiresApproval ? Visibility.Visible : Visibility.Collapsed;
        ApplyState(status.RequiresApproval ? OrbState.Approval : OrbState.Idle);
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var message = MessageBox.Text.Trim();
        if (message.Length == 0)
            return;

        try
        {
            ApplyState(OrbState.Thinking);
            ReplyText.Text = "Thinking…";
            var reply = await _bridge.ChatAsync(message);
            ReplyText.Text = reply;
            MessageBox.Text = string.Empty;
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ReplyText.Text = ex.Message;
            ApplyState(OrbState.Error);
        }
    }

    private async void ApproveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _bridge.ApproveLessonAsync();
            ReplyText.Text = "Lesson approved and saved to MEMORIA.";
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ReplyText.Text = ex.Message;
            ApplyState(OrbState.Error);
        }
    }

    private async void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _bridge.RejectLessonAsync();
            ReplyText.Text = "Lesson rejected.";
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ReplyText.Text = ex.Message;
            ApplyState(OrbState.Error);
        }
    }

    private void ExpandPanel()
    {
        Panel.Visibility = Visibility.Visible;
        Panel.Width = 360;
        PanelColumn.Width = new GridLength(372);
        AppWindow.Resize(new SizeInt32(514, 440));
    }

    private void CollapsePanel()
    {
        Panel.Visibility = Visibility.Collapsed;
        Panel.Width = 0;
        PanelColumn.Width = new GridLength(0);
        AppWindow.Resize(new SizeInt32(132, 132));
    }

    private void ApplyState(OrbState state)
    {
        _state = state;
        StateGlyph.Text = state switch
        {
            OrbState.Idle => "•",
            OrbState.Listening => "≈",
            OrbState.Thinking => "✦",
            OrbState.Approval => "!",
            OrbState.Error => "×",
            _ => "•"
        };
        OrbButton.Opacity = state == OrbState.Thinking ? 0.82 : 1.0;
    }
}

public enum OrbState
{
    Idle,
    Listening,
    Thinking,
    Approval,
    Error
}
