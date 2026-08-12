using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;

namespace BabyAI.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly BabyAIBridgeClient _bridge = new();
    private readonly DesktopSettingsStore _settings = new();
    private OrbState _state = OrbState.Idle;
    private bool _expanded;
    private bool _dragging;
    private bool _dragMoved;
    private Point _dragOrigin;
    private PointInt32 _windowOrigin;

    public MainWindow()
    {
        InitializeComponent();
        Title = "BabyAI";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ConfigureWindow();
        RestoreWindowPosition();
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

    private void RestoreWindowPosition()
    {
        var saved = _settings.Load();
        if (saved is not null)
            AppWindow.Move(new PointInt32(saved.X, saved.Y));
    }

    private async void OrbButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dragMoved)
        {
            _dragMoved = false;
            return;
        }

        try
        {
            _expanded = !_expanded;
            if (!_expanded)
            {
                await CollapsePanelAsync();
                ApplyState(OrbState.Idle);
                return;
            }

            await ExpandPanelAsync();
            ApplyState(OrbState.Thinking);
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            ReplyText.Text = ex.Message;
            ApplyState(OrbState.Error);
        }
    }

    private void OrbButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        _dragging = true;
        _dragMoved = false;
        _dragOrigin = point.Position;
        _windowOrigin = AppWindow.Position;
        OrbButton.CapturePointer(e.Pointer);
    }

    private void OrbButton_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        var point = e.GetCurrentPoint(Root).Position;
        var dx = (int)Math.Round(point.X - _dragOrigin.X);
        var dy = (int)Math.Round(point.Y - _dragOrigin.Y);
        if (Math.Abs(dx) + Math.Abs(dy) < 4)
            return;

        _dragMoved = true;
        AppWindow.Move(new PointInt32(_windowOrigin.X + dx, _windowOrigin.Y + dy));
    }

    private void OrbButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        OrbButton.ReleasePointerCapture(e.Pointer);
        var position = AppWindow.Position;
        _settings.Save(position.X, position.Y);
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

    private async Task ExpandPanelAsync()
    {
        Panel.Visibility = Visibility.Visible;
        Panel.Width = 360;
        PanelColumn.Width = new GridLength(372);
        AppWindow.Resize(new SizeInt32(514, 440));
        await AnimateOpacityAsync(Panel, 0, 1, 150);
    }

    private async Task CollapsePanelAsync()
    {
        await AnimateOpacityAsync(Panel, Panel.Opacity, 0, 110);
        Panel.Visibility = Visibility.Collapsed;
        Panel.Width = 0;
        PanelColumn.Width = new GridLength(0);
        AppWindow.Resize(new SizeInt32(132, 132));
    }

    private static Task AnimateOpacityAsync(UIElement target, double from, double to, int milliseconds)
    {
        var completion = new TaskCompletionSource();
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            target.Opacity = to;
            completion.TrySetResult();
        };
        storyboard.Begin();
        return completion.Task;
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
