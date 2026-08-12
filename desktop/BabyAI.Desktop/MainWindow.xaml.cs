using H.NotifyIcon;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace BabyAI.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly BabyAIBridgeClient _bridge = new();
    private readonly DesktopSettingsStore _settings = new();
    private readonly TrayIconService _tray;
    private readonly List<string> _conversation = [];
    private Storyboard? _orbStoryboard;
    private CancellationTokenSource? _chatCancellation;
    private OrbState _state = OrbState.Idle;
    private bool _expanded;
    private bool _dragging;
    private bool _dragMoved;
    private bool _exitRequested;
    private bool _busy;
    private Point _dragOrigin;
    private PointInt32 _windowOrigin;

    public MainWindow()
    {
        InitializeComponent();
        Title = "BabyAI";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        RuntimeText.Text = BuildRuntimeLabel();
        ConfigureWindow();
        RestoreWindowPosition();
        ApplyState(OrbState.Idle);
        _tray = new TrayIconService(this);
        AppWindow.Closing += (_, args) =>
        {
            if (_exitRequested)
                return;
            args.Cancel = true;
            HideToTray();
        };
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

    public void ShowFromTray()
    {
        WindowExtensions.Show(this);
        Activate();
    }

    public void HideToTray()
    {
        var position = AppWindow.Position;
        _settings.Save(position.X, position.Y);
        WindowExtensions.Hide(this);
    }

    public void RequestExit()
    {
        _exitRequested = true;
        _chatCancellation?.Cancel();
        _tray.Dispose();
        Close();
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
            MessageBox.Focus(FocusState.Programmatic);
            ApplyState(OrbState.Thinking);
            SetBusy(true);
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
        CoreStatusText.Text = "Core: connected";
        RuntimeText.Text = BuildRuntimeLabel();
        RetryButton.Visibility = Visibility.Collapsed;
        ApproveButton.Visibility = status.RequiresApproval ? Visibility.Visible : Visibility.Collapsed;
        RejectButton.Visibility = status.RequiresApproval ? Visibility.Visible : Visibility.Collapsed;
        ApplyState(status.RequiresApproval ? OrbState.Approval : OrbState.Idle);
    }

    private void MessageBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_busy || e.Key != VirtualKey.Enter)
            return;

        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        if ((shift & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down)
            return;

        e.Handled = true;
        SendButton_Click(sender, e);
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        var message = MessageBox.Text.Trim();
        if (message.Length == 0)
            return;

        _chatCancellation?.Dispose();
        _chatCancellation = new CancellationTokenSource();
        AppendConversation("You", message);
        MessageBox.Text = string.Empty;

        try
        {
            SetBusy(true, canStop: true);
            ApplyState(OrbState.Thinking);
            CoreStatusText.Text = "Core: thinking";
            RetryButton.Visibility = Visibility.Collapsed;
            ReplyText.Text = "Thinking…";
            var reply = await _bridge.ChatAsync(message, _chatCancellation.Token);
            ReplyText.Text = "Response complete.";
            AppendConversation("BabyAI", reply);
            await RefreshStatusAsync();
        }
        catch (OperationCanceledException)
        {
            CoreStatusText.Text = "Core: connected";
            ReplyText.Text = "Generation stopped.";
            AppendConversation("System", "Generation stopped by user.");
            ApplyState(OrbState.Idle);
        }
        catch (Exception ex)
        {
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

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_chatCancellation is null || _chatCancellation.IsCancellationRequested)
            return;

        CoreStatusText.Text = "Core: stopping";
        ReplyText.Text = "Stopping generation…";
        StopButton.IsEnabled = false;
        _chatCancellation.Cancel();
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        try
        {
            SetBusy(true);
            ApplyState(OrbState.Thinking);
            CoreStatusText.Text = "Core: checking";
            ReplyText.Text = "Checking BabyAI Core…";
            RetryButton.Visibility = Visibility.Collapsed;
            await RefreshStatusAsync();
            ReplyText.Text = "Core connection restored.";
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

    private async void ApproveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        try
        {
            SetBusy(true);
            await _bridge.ApproveLessonAsync();
            ReplyText.Text = "Lesson approved and saved to MEMORIA.";
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

    private async void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        try
        {
            SetBusy(true);
            await _bridge.RejectLessonAsync();
            ReplyText.Text = "Lesson rejected.";
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

    private void SetBusy(bool busy, bool canStop = false)
    {
        _busy = busy;
        SendButton.IsEnabled = !busy;
        RetryButton.IsEnabled = !busy;
        ApproveButton.IsEnabled = !busy;
        RejectButton.IsEnabled = !busy;
        MessageBox.IsEnabled = !busy;
        StopButton.Visibility = busy && canStop ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = busy && canStop;
    }

    private void AppendConversation(string speaker, string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;

        _conversation.Add($"{speaker}: {text}");
        if (_conversation.Count > 24)
            _conversation.RemoveAt(0);

        ConversationText.Text = string.Join("\n\n", _conversation);
        ConversationScroller.UpdateLayout();
        ConversationScroller.ChangeView(null, ConversationScroller.ScrollableHeight, null);
    }

    private static string BuildRuntimeLabel()
    {
        var provider = Environment.GetEnvironmentVariable("BABYAI_PROVIDER");
        if (string.IsNullOrWhiteSpace(provider))
            provider = "ollama";

        if (provider.Equals("echo", StringComparison.OrdinalIgnoreCase))
            return "Runtime: echo";

        var model = Environment.GetEnvironmentVariable("BABYAI_MODEL");
        if (string.IsNullOrWhiteSpace(model))
            model = "qwen3:8b";

        return $"Runtime: {provider} · {model}";
    }

    private void ShowBridgeError(Exception exception)
    {
        CoreStatusText.Text = "Core: unavailable";
        ReplyText.Text = FriendlyBridgeError(exception);
        RetryButton.Visibility = Visibility.Visible;
        ApplyState(OrbState.Error);
    }

    private static string FriendlyBridgeError(Exception exception)
    {
        var raw = exception.Message.Trim();
        var lower = raw.ToLowerInvariant();

        if (exception is TimeoutException || lower.Contains("timed out"))
            return "Ответ BabyAI занял слишком много времени. Проверь Ollama/модель и попробуй снова.";

        if (lower.Contains("no module named") || lower.Contains("babyai core is not installed"))
            return "BabyAI Core не найден. Запусти scripts\\windows\\start.ps1 или bootstrap.ps1, затем нажми Retry Core.";

        if (lower.Contains("could not start") && lower.Contains("python"))
            return "Python не удалось запустить. Проверь Python 3.11+ и снова запусти scripts\\windows\\start.ps1.";

        if (lower.Contains("ollama") || lower.Contains("11434") || lower.Contains("connection refused") || lower.Contains("actively refused"))
            return "Ollama недоступна. Запусти Ollama либо стартуй BabyAI с -Provider echo, затем нажми Retry Core.";

        if (raw.Length > 360)
            raw = raw[..360] + "…";

        return string.IsNullOrWhiteSpace(raw)
            ? "BabyAI Core недоступен. Запусти scripts\\windows\\start.ps1 и нажми Retry Core."
            : $"BabyAI Core недоступен: {raw}";
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
        _orbStoryboard?.Stop();

        StateGlyph.Text = state switch
        {
            OrbState.Idle => "•",
            OrbState.Listening => "≈",
            OrbState.Thinking => "✦",
            OrbState.Approval => "!",
            OrbState.Error => "×",
            _ => "•"
        };

        var visual = state switch
        {
            OrbState.Idle => new OrbVisual(1.035, 0.26, 2600, Color.FromArgb(255, 124, 141, 255), Color.FromArgb(255, 184, 200, 255), Color.FromArgb(255, 109, 124, 255)),
            OrbState.Listening => new OrbVisual(1.09, 0.48, 850, Color.FromArgb(255, 99, 220, 255), Color.FromArgb(255, 126, 220, 255), Color.FromArgb(255, 65, 151, 255)),
            OrbState.Thinking => new OrbVisual(1.075, 0.58, 650, Color.FromArgb(255, 174, 122, 255), Color.FromArgb(255, 163, 132, 255), Color.FromArgb(255, 111, 90, 255)),
            OrbState.Approval => new OrbVisual(1.055, 0.62, 1100, Color.FromArgb(255, 255, 190, 86), Color.FromArgb(255, 255, 204, 118), Color.FromArgb(255, 227, 148, 44)),
            OrbState.Error => new OrbVisual(1.045, 0.64, 420, Color.FromArgb(255, 255, 96, 113), Color.FromArgb(255, 255, 132, 145), Color.FromArgb(255, 208, 62, 84)),
            _ => new OrbVisual(1.035, 0.26, 2600, Color.FromArgb(255, 124, 141, 255), Color.FromArgb(255, 184, 200, 255), Color.FromArgb(255, 109, 124, 255)),
        };

        MidStop.Color = visual.Mid;
        EdgeStop.Color = visual.Edge;
        OrbGlow.Fill = new SolidColorBrush(visual.Glow);
        OrbRing.Stroke = new SolidColorBrush(Color.FromArgb(210, visual.Glow.R, visual.Glow.G, visual.Glow.B));
        OrbButton.Opacity = state == OrbState.Thinking ? 0.94 : 1.0;
        _orbStoryboard = CreateOrbPulseStoryboard(visual.Scale, visual.GlowOpacity, visual.DurationMs);
        _orbStoryboard.Begin();
    }

    private Storyboard CreateOrbPulseStoryboard(double peakScale, double peakGlowOpacity, int durationMs)
    {
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));

        var scaleX = new DoubleAnimation { From = 1.0, To = peakScale, Duration = duration, EnableDependentAnimation = true };
        Storyboard.SetTarget(scaleX, OrbScale);
        Storyboard.SetTargetProperty(scaleX, "ScaleX");
        storyboard.Children.Add(scaleX);

        var scaleY = new DoubleAnimation { From = 1.0, To = peakScale, Duration = duration, EnableDependentAnimation = true };
        Storyboard.SetTarget(scaleY, OrbScale);
        Storyboard.SetTargetProperty(scaleY, "ScaleY");
        storyboard.Children.Add(scaleY);

        var glow = new DoubleAnimation { From = Math.Max(0.12, peakGlowOpacity * 0.45), To = peakGlowOpacity, Duration = duration, EnableDependentAnimation = true };
        Storyboard.SetTarget(glow, OrbGlow);
        Storyboard.SetTargetProperty(glow, "Opacity");
        storyboard.Children.Add(glow);

        var ring = new DoubleAnimation { From = 0.48, To = 0.96, Duration = duration, EnableDependentAnimation = true };
        Storyboard.SetTarget(ring, OrbRing);
        Storyboard.SetTargetProperty(ring, "Opacity");
        storyboard.Children.Add(ring);

        return storyboard;
    }

    private sealed record OrbVisual(double Scale, double GlowOpacity, int DurationMs, Color Glow, Color Mid, Color Edge);
}

public enum OrbState
{
    Idle,
    Listening,
    Thinking,
    Approval,
    Error
}
