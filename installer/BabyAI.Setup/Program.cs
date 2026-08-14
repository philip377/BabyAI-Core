using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using IOPath = System.IO.Path;

namespace BabyAI.Setup;

internal static class Program
{
    internal static string? BundleRoot { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        BundleRoot = ResolveBundleRoot(args);
        var app = new Application();
        app.Run(new SetupWindow());
    }

    private static string? ResolveBundleRoot(string[] args)
    {
        var arg = args.FirstOrDefault(x => x.StartsWith("--bundle=", StringComparison.OrdinalIgnoreCase));
        if (arg is not null)
        {
            return IOPath.GetFullPath(arg["--bundle=".Length..].Trim('"'));
        }

        var environment = Environment.GetEnvironmentVariable("BABYAI_BUNDLE_ROOT");
        if (!string.IsNullOrWhiteSpace(environment))
        {
            return IOPath.GetFullPath(environment);
        }

        var sibling = IOPath.Combine(AppContext.BaseDirectory, "bundle");
        return Directory.Exists(sibling) ? sibling : null;
    }
}

internal sealed class SetupWindow : Window
{
    private readonly ProgressBar _progress;
    private readonly TextBlock _status;
    private readonly Button _install;
    private bool _installed;
    private string? _desktopPath;

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
            Fill = new RadialGradientBrush(Color.FromRgb(212, 104, 255), Color.FromRgb(67, 41, 170)),
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
        stack.Children.Add(Step("1", "Проверка системы", "Целостность релиза и доступное место"));
        stack.Children.Add(Step("2", "Установка ядра", "Самодостаточный Desktop + native runtime"));
        stack.Children.Add(Step("3", "Подготовка мозга", "Сохраняем модель и выбираем backend автоматически"));

        _status = Text(Program.BundleRoot is null ? "Релизный пакет не найден" : "Готово к установке", 13, FontWeights.Medium, new Thickness(0, 26, 0, 8), Color.FromRgb(158, 166, 198));
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
            Cursor = System.Windows.Input.Cursors.Hand,
            IsEnabled = Program.BundleRoot is not null
        };
        _install.Click += InstallClicked;
        stack.Children.Add(_install);
        stack.Children.Add(Text("v0.1 · Windows x64 · локальная установка", 12, FontWeights.Normal, new Thickness(0, 16, 0, 0), Color.FromRgb(103, 110, 145)));
    }

    private async void InstallClicked(object sender, RoutedEventArgs e)
    {
        if (_installed)
        {
            if (_desktopPath is not null && File.Exists(_desktopPath))
            {
                Process.Start(new ProcessStartInfo(_desktopPath) { UseShellExecute = true });
                Close();
            }
            return;
        }

        if (Program.BundleRoot is null)
        {
            return;
        }

        _install.IsEnabled = false;
        try
        {
            var progress = new Progress<(string Message, int Value)>(step =>
            {
                _status.Text = step.Message;
                _progress.Value = step.Value;
            });

            _desktopPath = await Task.Run(() => InstallerEngine.Install(Program.BundleRoot, progress));
            _status.Text = "BabyAI установлен и готов к запуску";
            _progress.Value = 100;
            _install.Content = "Запустить BabyAI";
            _installed = true;
            _install.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _status.Text = $"Ошибка установки: {ex.Message}";
            _progress.Value = 0;
            _install.Content = "Повторить установку";
            _install.IsEnabled = true;
        }
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

internal static class InstallerEngine
{
    private sealed record ReleaseManifest(string Version, bool PythonIncluded);

    public static string Install(string bundleRoot, IProgress<(string Message, int Value)> progress)
    {
        bundleRoot = IOPath.GetFullPath(bundleRoot);
        progress.Report(("Проверяю целостность релиза…", 12));
        VerifyChecksums(bundleRoot);
        var manifest = ReadManifest(bundleRoot);
        if (!manifest.PythonIncluded)
        {
            throw new InvalidOperationException("Этот установщик требует self-contained Python runtime.");
        }

        var required = new[] { "app", "runtime", "wheels", "python" };
        foreach (var directory in required)
        {
            if (!Directory.Exists(IOPath.Combine(bundleRoot, directory)))
            {
                throw new InvalidDataException($"В релизе отсутствует папка {directory}.");
            }
        }

        var installRoot = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabyAI");
        var versionsRoot = IOPath.Combine(installRoot, "versions");
        var versionDir = IOPath.Combine(versionsRoot, manifest.Version);
        var tempDir = versionDir + ".installing";
        Directory.CreateDirectory(versionsRoot);

        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);

        try
        {
            progress.Report(("Устанавливаю приложение…", 35));
            CopyDirectory(IOPath.Combine(bundleRoot, "app"), IOPath.Combine(tempDir, "app"));
            progress.Report(("Устанавливаю native runtime…", 52));
            CopyDirectory(IOPath.Combine(bundleRoot, "runtime"), IOPath.Combine(tempDir, "runtime"));
            progress.Report(("Устанавливаю локальный Python…", 68));
            CopyDirectory(IOPath.Combine(bundleRoot, "python"), IOPath.Combine(tempDir, "python"));
            CopyDirectory(IOPath.Combine(bundleRoot, "wheels"), IOPath.Combine(tempDir, "wheels"));

            var pythonExe = IOPath.Combine(tempDir, "python", "python.exe");
            if (!File.Exists(pythonExe)) throw new InvalidDataException("Bundled python.exe не найден.");

            progress.Report(("Фиксирую атомарную версию…", 82));
            if (Directory.Exists(versionDir)) Directory.Delete(versionDir, true);
            Directory.Move(tempDir, versionDir);

            progress.Report(("Проверяю новую версию BabyAI…", 90));
            var desktop = IOPath.Combine(versionDir, "app", "BabyAI.Desktop.exe");
            if (!File.Exists(desktop)) throw new InvalidDataException("BabyAI.Desktop.exe не найден в установленной версии.");

            RollbackIntegration.CommitVersionSwitch(installRoot, manifest.Version, versionDir);
            PreserveOrCreateLaunchSettings(installRoot);
            progress.Report(("Ищу локальную GGUF-модель…", 94));
            ModelDiscovery.TryAdoptLocalModel(installRoot);

            progress.Report(("Создаю ярлыки BabyAI…", 97));
            ShellIntegration.CreateShortcuts(desktop);
            return desktop;
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private static void VerifyChecksums(string bundleRoot)
    {
        var sumsPath = IOPath.Combine(bundleRoot, "SHA256SUMS.txt");
        if (!File.Exists(sumsPath)) throw new InvalidDataException("SHA256SUMS.txt не найден.");

        var prefix = bundleRoot.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar) + IOPath.DirectorySeparatorChar;
        foreach (var raw in File.ReadLines(sumsPath))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var split = raw.Split("  ", 2, StringSplitOptions.None);
            if (split.Length != 2 || split[0].Length != 64) throw new InvalidDataException("Некорректная строка SHA256SUMS.txt.");
            var path = IOPath.GetFullPath(IOPath.Combine(bundleRoot, split[1].Replace('/', IOPath.DirectorySeparatorChar)));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь checksum выходит за пределы релиза.");
            if (!File.Exists(path)) throw new FileNotFoundException("Файл релиза отсутствует.", path);
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(split[0], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"SHA-256 не совпадает: {split[1]}");
        }
    }

    private static ReleaseManifest ReadManifest(string bundleRoot)
    {
        var path = IOPath.Combine(bundleRoot, "release.json");
        if (!File.Exists(path)) throw new InvalidDataException("release.json не найден.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var version = root.GetProperty("version").GetString();
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("release.json не содержит version.");
        var pythonIncluded = root.TryGetProperty("python_included", out var python) && python.GetBoolean();
        return new ReleaseManifest(version, pythonIncluded);
    }

    private static void PreserveOrCreateLaunchSettings(string installRoot)
    {
        var path = IOPath.Combine(installRoot, "launch.json");
        if (File.Exists(path)) return;
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            provider = "native",
            acceleration = "auto",
            model = ""
        }, JsonOptions));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, IOPath.Combine(destination, IOPath.GetFileName(file)), true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, IOPath.Combine(destination, IOPath.GetFileName(directory)));
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
