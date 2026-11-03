using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;

namespace BabyAI.Desktop;

internal enum GingerCompanionPhase
{
    Idle,
    Checking,
    Reading,
    Reviewing,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// Opt-in visual companion for observed Ginger task stages. It never runs a
/// Ginger command and never displays supplier names or private model reasoning.
/// </summary>
internal sealed class GingerCompanionWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPosition
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPosition position);

    private readonly AgentProfile _profile;
    private readonly AgentPreferences _preferences;
    private readonly DispatcherTimer _visibilityTimer;
    private bool _capturing;
    private bool _hidden;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int count);
    public event Action<PointInt32>? PositionSaved;
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    private readonly TextBlock _status;
    private readonly TextBlock _face;
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _blinkResetTimer;
    private GingerCompanionPhase _phase = GingerCompanionPhase.Idle;
    private bool _dragging;
    private CursorPosition _cursorOrigin;
    private PointInt32 _windowOrigin;

    public GingerCompanionWindow(AgentProfile profile, AgentPreferences preferences)
    {
        _profile = profile;
        _preferences = preferences;
        Title = "UNIX · " + profile.Name;
        // Keep the pet out of the taskbar and Alt+Tab; it is controlled from UNIX.
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Resize(new SizeInt32(260, 215));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 32, 30, 49)),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(14),
            RowSpacing = 10
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new TextBlock
        {
            Text = profile.Name,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 240, 236, 255)),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        AttachDrag(heading);
        header.Children.Add(heading);
        var close = new Button { Content = "×" };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        root.Children.Add(header);

        var avatar = new Grid { Height = 70, Width = 70, HorizontalAlignment = HorizontalAlignment.Center };
        avatar.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(profile.Appearance switch { "mint" => Color.FromArgb(255, 65, 150, 130), "amber" => Color.FromArgb(255, 180, 125, 50), _ => Color.FromArgb(255, 126, 112, 221) }),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 180, 168, 255)),
            StrokeThickness = 2
        });
        _face = new TextBlock
        {
            Text = "• ᴗ •",
            FontSize = 22,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        avatar.Children.Add(_face);
        AttachDrag(avatar);
        Grid.SetRow(avatar, 1);
        root.Children.Add(avatar);

        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(255, 235, 230, 250)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 12
        };
        var bubble = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 59, 53, 88)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 8, 10, 8),
            Child = _status
        };
        Grid.SetRow(bubble, 2);
        root.Children.Add(bubble);
        Content = root;
        SetStatus(GingerCompanionPhase.Idle);

        // One small blink every few seconds. Both timers stop when the window closes.
        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.4) };
        _blinkResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _blinkTimer.Tick += (_, _) =>
        {
            if (_phase is not (GingerCompanionPhase.Idle or GingerCompanionPhase.Completed))
                return;
            _face.Text = "– ᴗ –";
            _blinkResetTimer.Stop();
            _blinkResetTimer.Start();
        };
        _blinkResetTimer.Tick += (_, _) =>
        {
            _blinkResetTimer.Stop();
            SetStatus(_phase);
        };
        if (preferences.Animate) _blinkTimer.Start();
        if (profile.X.HasValue && profile.Y.HasValue)
        {
            var desired = new PointInt32(profile.X.Value, profile.Y.Value);
            var area = DisplayArea.GetFromPoint(desired, DisplayAreaFallback.Primary).WorkArea;
            AppWindow.Move(new PointInt32(Math.Clamp(desired.X, area.X, Math.Max(area.X, area.X + area.Width - 260)),
                Math.Clamp(desired.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - 215))));
        }
        _visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _visibilityTimer.Tick += (_, _) => UpdateVisibility();
        _visibilityTimer.Start();
        Closed += (_, _) =>
        {
            _blinkTimer.Stop();
            _blinkResetTimer.Stop();
            _visibilityTimer.Stop();
        };
    }

    public static string DescribePhase(GingerCompanionPhase phase) => phase switch
    {
        GingerCompanionPhase.Checking => "Начинаю проверку. Работаю только на чтение.",
        GingerCompanionPhase.Reading => "Считываю данные Ginger…",
        GingerCompanionPhase.Reviewing => "Проверяю возможные совпадения…",
        GingerCompanionPhase.Completed => "Этап завершён. Результат — в панели Ginger.",
        GingerCompanionPhase.Cancelled => "Проверка остановлена по запросу.",
        GingerCompanionPhase.Failed => "Проверка не завершена. Открой журнал.",
        _ => "Ожидаю задачу. Данные Ginger не меняю."
    };

    public void SetStatus(GingerCompanionPhase phase)
    {
        _phase = phase;
        _status.Text = _preferences.Quiet ? _profile.Name : _profile.Id == "ginger" ? DescribePhase(phase)
            : phase == GingerCompanionPhase.Reading ? "Выполняю задачу через UNIX…"
            : phase == GingerCompanionPhase.Completed ? "Ответ готов в чате UNIX."
            : phase == GingerCompanionPhase.Failed ? "Задача остановлена. Проверь чат UNIX." : "Ожидаю задачу в чате UNIX.";
        _face.Text = phase switch
        {
            GingerCompanionPhase.Failed => "• _ •",
            GingerCompanionPhase.Checking or GingerCompanionPhase.Reading
                or GingerCompanionPhase.Reviewing => "• ◡ •",
            GingerCompanionPhase.Completed => "✦ ᴗ ✦",
            _ => "• ᴗ •"
        };
    }

    // During screen capture, don't let our always-on-top pet cover Ginger cells.
    public void SetCaptureMode(bool capturing)
    {
        _capturing = capturing;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        bool hidden = _capturing;
        var foreground = GetForegroundWindow();
        var own = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowClass = new System.Text.StringBuilder(256);
        GetClassName(foreground, windowClass, windowClass.Capacity);
        bool shell = windowClass.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd";
        if (!hidden && !shell && _preferences.HideInFullscreen && foreground != IntPtr.Zero && foreground != own
            && IsWindowVisible(foreground) && GetWindowRect(foreground, out var rect))
        {
            var area = DisplayArea.GetFromPoint(new PointInt32(rect.Left + 1, rect.Top + 1), DisplayAreaFallback.Primary).OuterBounds;
            hidden = rect.Left <= area.X && rect.Top <= area.Y && rect.Right >= area.X + area.Width && rect.Bottom >= area.Y + area.Height;
        }
        if (hidden == _hidden) return;
        _hidden = hidden;
        if (hidden) AppWindow.Hide(); else AppWindow.Show(false);
    }

    public static void AttachDesktopDrag(UIElement element, Action<PointInt32> dropped)
    {
        CursorPosition start = default;
        bool dragging = false;
        element.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed || !GetCursorPos(out start)) return;
            dragging = element.CapturePointer(e.Pointer);
        };
        element.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            element.ReleasePointerCapture(e.Pointer);
            if (GetCursorPos(out var end) && Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) > 30)
                dropped(new PointInt32(end.X - 130, end.Y - 40));
        };
        element.PointerCaptureLost += (_, _) => dragging = false;
    }

    private void AttachDrag(UIElement handle)
    {
        handle.PointerPressed += DragPressed;
        handle.PointerMoved += DragMoved;
        handle.PointerReleased += DragReleased;
        handle.PointerCaptureLost += (_, _) => _dragging = false;
    }

    private void DragPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed
            || !GetCursorPos(out _cursorOrigin))
            return;

        _dragging = true;
        _windowOrigin = AppWindow.Position;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void DragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !GetCursorPos(out var cursor))
            return;

        AppWindow.Move(new PointInt32(
            _windowOrigin.X + cursor.X - _cursorOrigin.X,
            _windowOrigin.Y + cursor.Y - _cursorOrigin.Y));
    }

    private void DragReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        PositionSaved?.Invoke(AppWindow.Position);
    }
}
