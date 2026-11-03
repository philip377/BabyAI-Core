using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;

namespace BabyAI.Desktop;

/// <summary>
/// User-controlled companion preview. It never performs Ginger operations.
/// Status text is explicit UI state, not hidden model reasoning.
/// </summary>
internal sealed class GingerCompanionWindow : Window
{
    private readonly TextBlock _status;
    private bool _dragging;
    private Windows.Foundation.Point _pointerOrigin;
    private PointInt32 _windowOrigin;

    public GingerCompanionWindow()
    {
        Title = "UNIX · Ginger";
        AppWindow.Resize(new SizeInt32(260, 180));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 32, 30, 49)),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(14),
            RowSpacing = 8
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
        heading.PointerPressed += DragPressed;
        heading.PointerMoved += DragMoved;
        heading.PointerReleased += DragReleased;
        header.Children.Add(heading);
        var close = new Button { Content = "×" };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        root.Children.Add(header);

        var face = new Grid { Height = 68, Width = 68, HorizontalAlignment = HorizontalAlignment.Center };
        face.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Color.FromArgb(255, 126, 112, 221)),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 180, 168, 255)),
            StrokeThickness = 2
        });
        face.Children.Add(new TextBlock
        {
            Text = "• ᴗ •",
            FontSize = 22,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(face, 1);
        root.Children.Add(face);

        _status = new TextBlock
        {
            Text = "Ожидаю задачи. Данные Ginger не изменяю.",
            Foreground = new SolidColorBrush(Color.FromArgb(255, 235, 230, 250)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 12
        };
        Grid.SetRow(_status, 2);
        root.Children.Add(_status);
        Content = root;
    }

    private void DragPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _pointerOrigin = e.GetCurrentPoint(null).Position;
        _windowOrigin = AppWindow.Position;
        ((UIElement)sender).CapturePointer(e.Pointer);
    }

    private void DragMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var point = e.GetCurrentPoint(null).Position;
        AppWindow.Move(new PointInt32(
            _windowOrigin.X + (int)Math.Round(point.X - _pointerOrigin.X),
            _windowOrigin.Y + (int)Math.Round(point.Y - _pointerOrigin.Y)));
    }

    private void DragReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
    }
}
