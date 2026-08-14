using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace BabyAI.Setup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new Application();
        app.Run(new SetupWindow());
    }
}

internal sealed class SetupWindow : Window
{
    private readonly ProgressBar _progress;
    private readonly TextBlock _status;
    private readonly Button _install;

    public SetupWindow()
    {
        Title = "BabyAI Setup";
        Width = 980;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(7, 9, 18));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text");

        var root = new Grid { Margin = new Thickness(28) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.43, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.57, GridUnitType.Star) });
        Content = root;

        var hero = Card();
        hero.Margin = new Thickness(0, 0, 18, 0);
        Grid.SetColumn(hero, 0);
        root.Children.Add(hero);

        var heroStack = new StackPanel { Margin = new Thickness(34), VerticalAlignment = VerticalAlignment.Center };
        hero.Child = heroStack;
        var orb = new Ellipse
        {
            Width = 156,
            Height = 156,
            Fill = new RadialGradientBrush(
                Color.FromRgb(212, 104, 255),
                Color.FromRgb(67, 41, 170)),
            Effect = new DropShadowEffect { BlurRadius = 44, ShadowDepth = 0, Opacity = 0.85, Color = Color.FromRgb(142, 77, 255) },
            HorizontalAlignment = HorizontalAlignment.Left
        };
        heroStack.Children.Add(orb);
        heroStack.Children.Add(Text("BabyAI", 34, FontWeights.SemiBold, new Thickness(0, 28, 0, 4)));
        heroStack.Children.Add(Text("Локальный ИИ, который живёт рядом с вами.", 16, FontWeights.Normal, new Thickness(0, 0, 0, 18), Color.FromRgb(183, 188, 214)));
        heroStack.Children.Add(Text("Без Ollama. Без ручного Python.\nУстановка и выбор железа — автоматически.", 14, FontWeights.Normal, new Thickness(0), Color.FromRgb(132, 140, 175)));

        var body = Card();
        Grid.SetColumn(body, 1);
        root.Children.Add(body);

        var stack = new StackPanel { Margin = new Thickness(40), VerticalAlignment = VerticalAlignment.Center };
        body.Child = stack;
        stack.Children.Add(Text("Установка BabyAI", 30, FontWeights.SemiBold, new Thickness(0, 0, 0, 10)));
        stack.Children.Add(Text("Подготовим приложение, локальный мозг и оптимальный backend для этого компьютера.", 15, FontWeights.Normal, new Thickness(0, 0, 0, 30), Color.FromRgb(170, 176, 204)));
        stack.Children.Add(Step("1", "Проверка системы", "CPU, GPU, Vulkan и доступное место"));
        stack.Children.Add(Step("2", "Установка ядра", "Самодостаточный Desktop + native runtime"));
        stack.Children.Add(Step("3", "Подготовка мозга", "Поиск или безопасная загрузка модели"));

        _status = Text("Готово к установке", 13, FontWeights.Medium, new Thickness(0, 26, 0, 8), Color.FromRgb(158, 166, 198));
        stack.Children.Add(_status);
        _progress = new ProgressBar { Height = 8, Minimum = 0, Maximum = 100, Value = 0, Margin = new Thickness(0, 0, 0, 24) };
        stack.Children.Add(_progress);

        _install = new Button
        {
            Content = "Установить BabyAI",
            Height = 48,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(Color.FromRgb(115, 76, 232)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        _install.Click += InstallClicked;
        stack.Children.Add(_install);
        stack.Children.Add(Text("v0.1 · Windows x64 · локальная установка", 12, FontWeights.Normal, new Thickness(0, 16, 0, 0), Color.FromRgb(103, 110, 145)));
    }

    private async void InstallClicked(object sender, RoutedEventArgs e)
    {
        _install.IsEnabled = false;
        var phases = new[]
        {
            ("Проверяю систему и совместимость…", 18),
            ("Готовлю защищённую папку установки…", 36),
            ("Распаковываю BabyAI runtime…", 61),
            ("Проверяю локальный мозг…", 78),
            ("Настраиваю оптимальный backend…", 92),
            ("BabyAI готов к запуску", 100)
        };

        // v1 UI shell. Real bundle extraction/hardware/model actions are wired in the next installer slice.
        foreach (var (label, value) in phases)
        {
            _status.Text = label;
            _progress.Value = value;
            await Task.Delay(240);
        }

        _install.Content = "Запустить BabyAI";
        _install.IsEnabled = true;
    }

    private static Border Card() => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(218, 15, 18, 34)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(80, 126, 104, 224)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(24),
        Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.26, Color = Colors.Black }
    };

    private static Border Step(string index, string title, string detail)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var badge = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromArgb(90, 115, 76, 232)),
            Child = new TextBlock { Text = index, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold }
        };
        grid.Children.Add(badge);
        var labels = new StackPanel();
        labels.Children.Add(Text(title, 15, FontWeights.SemiBold, new Thickness(0, 0, 0, 2)));
        labels.Children.Add(Text(detail, 12, FontWeights.Normal, new Thickness(0), Color.FromRgb(128, 136, 170)));
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);
        return new Border { Child = grid };
    }

    private static TextBlock Text(string value, double size, FontWeight weight, Thickness margin, Color? color = null) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = weight,
        Margin = margin,
        Foreground = new SolidColorBrush(color ?? Colors.White),
        TextWrapping = TextWrapping.Wrap
    };
}
