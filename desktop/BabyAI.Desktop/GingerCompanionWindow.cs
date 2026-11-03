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

    private readonly TextBlock _status;
    private readonly TextBlock _face;
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _blinkResetTimer;
    private GingerCompanionPhase _phase = GingerCompanionPhase.Idle;
    private bool _dragging;
    private CursorPosition _cursorOrigin;
    private PointInt32 _windowOrigin;

    public GingerCompanionWindow()
    {
        Title = "UNIX · Ginger";
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
            Text = "Ginger Agent · превью",
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
            Fill = new SolidColorBrush(Color.FromArgb(255, 126, 112, 221)),
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
        _blinkTimer.Start();
        Closed += (_, _) =>
        {
            _blinkTimer.Stop();
            _blinkResetTimer.Stop();
        };
    }

    public static string DescribePhase(GingerCompanionPhase phase) => phase switch
    {
        GingerCompanionPhase.Checking => "Начинаю проверку. Работаю только на чтение.",
        GingerCompanionPhase.Reading => "Считываю данные Ginger…",
        GingerCompanionPhase.Reviewing => "Проверяю возможные совпадения…",
        GingerCompanionPhase.Completed => "Готово! Данные Ginger не менялись.",
        GingerCompanionPhase.Cancelled => "Проверка остановлена по запросу.",
        GingerCompanionPhase.Failed => "Проверка не завершена. Открой журнал.",
        _ => "Ожидаю задачу. Данные Ginger не меняю."
    };

    public void SetStatus(GingerCompanionPhase phase)
    {
        _phase = phase;
        _status.Text = DescribePhase(phase);
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
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = !capturing;
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
    }
}
