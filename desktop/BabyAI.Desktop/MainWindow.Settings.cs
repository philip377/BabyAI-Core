using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private readonly DesktopUiSettingsStore _uiSettings = new();

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyStoredUiSettings();
        _ = CheckForUpdatesOnStartupAsync();
    }

    private void ApplyStoredUiSettings()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;

        presenter.IsAlwaysOnTop = _uiSettings.Load().AlwaysOnTop;
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var contentHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        var menu = new StackPanel
        {
            Width = 152,
            Spacing = 5,
        };
        var buttons = new List<Button>();

        void SelectSection(Button selected, Func<UIElement> build)
        {
            foreach (var button in buttons)
            {
                button.Opacity = ReferenceEquals(button, selected) ? 1.0 : 0.62;
                button.Background = ReferenceEquals(button, selected)
                    ? new SolidColorBrush(Color.FromArgb(24, 124, 141, 255))
                    : new SolidColorBrush(Color.FromArgb(0, 255, 255, 255));
            }

            contentHost.Content = build();
        }

        Button AddSection(string title, Func<UIElement> build)
        {
            var button = new Button
            {
                Content = title,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(0),
            };
            buttons.Add(button);
            menu.Children.Add(button);
            button.Click += (_, _) => SelectSection(button, build);
            return button;
        }

        var generalButton = AddSection("Общие", BuildGeneralSettings);
        AddSection("Обновления", BuildUpdateSettings);
        AddSection("Мозг", BuildBrainSettings);
        AddSection("Производительность", BuildPerformanceSettings);
        AddSection("Интерфейс", BuildInterfaceSettings);
        AddSection("Диагностика", BuildDiagnosticsSettings);

        var layout = new Grid
        {
            MinWidth = 590,
            Height = 430,
            ColumnSpacing = 14,
        };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(152) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(menu, 0);
        layout.Children.Add(menu);

        var scroller = new ScrollViewer
        {
            Content = contentHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetColumn(scroller, 1);
        layout.Children.Add(scroller);

        SelectSection(generalButton, BuildGeneralSettings);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "BabyAI · Настройки",
            Content = layout,
            CloseButtonText = "Готово",
            DefaultButton = ContentDialogButton.Close,
        };

        await dialog.ShowAsync();
    }

    private UIElement BuildGeneralSettings()
    {
        var panel = CreateSettingsPage(
            "Общие",
            "Базовые параметры локального desktop-компаньона.");
        panel.Children.Add(CreateSettingsInfo(
            "Данные BabyAI",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BabyAI")));
        panel.Children.Add(CreateSettingsInfo(
            "Запуск",
            "BabyAI работает локально и сворачивается в системный трей при закрытии окна."));
        panel.Children.Add(CreateSettingsNote(
            "Автозапуск Windows добавим отдельным безопасным переключателем в следующем проходе."));
        return panel;
    }

    private UIElement BuildBrainSettings()
    {
        var provider = ReadEnvironment("BABYAI_PROVIDER", "ollama").ToLowerInvariant();
        var panel = CreateSettingsPage(
            "Мозг",
            "Какой локальный провайдер и модель используются сейчас.");
        panel.Children.Add(CreateSettingsInfo("Провайдер", provider));

        if (provider.Equals("native", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(CreateSettingsInfo(
                "GGUF модель",
                ReadEnvironment("BABYAI_NATIVE_MODEL", "Путь не задан")));
            panel.Children.Add(CreateSettingsInfo(
                "Native runtime",
                ReadEnvironment("BABYAI_NATIVE_RUNTIME", "Путь не задан")));
        }
        else
        {
            panel.Children.Add(CreateSettingsInfo(
                "Модель",
                ReadEnvironment("BABYAI_MODEL", "qwen3:8b")));
        }

        panel.Children.Add(CreateSettingsNote(
            "Смена провайдера и модели пока остаётся параметром запуска — Settings v1 ничего не перезапускает сам."));
        return panel;
    }

    private UIElement BuildPerformanceSettings()
    {
        var provider = ReadEnvironment("BABYAI_PROVIDER", "ollama").ToLowerInvariant();
        var panel = CreateSettingsPage(
            "Производительность",
            "Режим вычислений локальной модели.");

        if (!provider.Equals("native", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(CreateSettingsInfo(
                "Текущий режим",
                "Управляется выбранным провайдером"));
            panel.Children.Add(CreateSettingsNote(
                "Переключатель CPU/GPU доступен для встроенного native GGUF runtime."));
            return panel;
        }

        (string Mode, string Label)[] modes =
        [
            ("cpu", "Процессор (CPU)"),
            ("vulkan", "Видеокарта (GPU · Vulkan)"),
            ("hybrid", "GPU + CPU · сбалансированный"),
            ("auto", "Автоматически · GPU, иначе CPU"),
        ];
        var selectedMode = ReadEnvironment("BABYAI_NATIVE_ACCELERATION", "auto").ToLowerInvariant();
        if (!modes.Any(option => option.Mode == selectedMode))
            selectedMode = "auto";

        var selector = new ComboBox
        {
            Header = "Вычислительное устройство",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = !_busy,
            Margin = new Thickness(2, 1, 2, 1),
        };
        foreach (var option in modes)
        {
            selector.Items.Add(new ComboBoxItem
            {
                Content = option.Label,
                Tag = option.Mode,
            });
        }
        selector.SelectedIndex = Array.FindIndex(
            modes,
            option => option.Mode == selectedMode);

        var status = new TextBlock
        {
            Text = PerformanceModeDescription(selectedMode)
                + (_busy ? " Дождитесь завершения текущего ответа, чтобы сменить режим." : string.Empty),
            FontSize = 11,
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 5, 2, 1),
        };
        var control = new StackPanel { Spacing = 3 };
        control.Children.Add(selector);
        control.Children.Add(status);
        panel.Children.Add(CreateSettingsCard(control));

        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is not ComboBoxItem item || item.Tag is not string mode)
                return;

            try
            {
                InstalledRuntimeBootstrap.SaveAccelerationPreference(mode);
                _bridge.RestartWorker();
                RuntimeText.Text = BuildRuntimeLabel();
                status.Text = PerformanceModeDescription(mode)
                    + " Новый режим применится к следующему запросу.";
                StartupDiagnostics.Log($"Native acceleration changed from Settings: mode={mode}");
            }
            catch (Exception ex)
            {
                status.Text = $"Не удалось сохранить режим: {ex.Message}";
                StartupDiagnostics.Log("Native acceleration change failed", ex);
            }
        };

        panel.Children.Add(CreateSettingsInfo(
            "Vulkan runtime",
            ReadEnvironment("BABYAI_NATIVE_VULKAN_RUNTIME", "Не найден")));
        panel.Children.Add(CreateSettingsNote(
            "GPU использует максимум доступных слоёв модели. GPU + CPU переносит 20 слоёв на видеокарту, "
            + "а остальные оставляет процессору — этот режим полезен при ограниченной видеопамяти."));
        return panel;
    }

    private static string PerformanceModeDescription(string mode)
    {
        return mode switch
        {
            "cpu" => "Все слои модели обрабатываются процессором.",
            "vulkan" => "Модель максимально переносится на совместимую Vulkan-видеокарту.",
            "hybrid" => "Часть модели работает на GPU, оставшаяся часть — на CPU.",
            _ => "BabyAI пробует GPU и безопасно возвращается на CPU, если Vulkan недоступен.",
        };
    }

    private UIElement BuildInterfaceSettings()
    {
        var panel = CreateSettingsPage(
            "Интерфейс",
            "Поведение Orb и desktop-окна.");
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        var alwaysOnTop = new ToggleSwitch
        {
            Header = "Всегда поверх окон",
            IsOn = presenter?.IsAlwaysOnTop ?? true,
            OnContent = "Включено",
            OffContent = "Выключено",
            Margin = new Thickness(2, 2, 2, 4),
        };
        alwaysOnTop.Toggled += (_, _) =>
        {
            if (AppWindow.Presenter is OverlappedPresenter currentPresenter)
                currentPresenter.IsAlwaysOnTop = alwaysOnTop.IsOn;
            var current = _uiSettings.Load();
            _uiSettings.Save(new DesktopUiSettings(alwaysOnTop.IsOn, current.CheckForUpdatesOnStartup));
        };
        panel.Children.Add(CreateSettingsCard(alwaysOnTop));
        panel.Children.Add(CreateSettingsInfo(
            "Размер окна",
            "Адаптивный · BabyAI автоматически помещает панель в рабочую область текущего монитора."));
        panel.Children.Add(CreateSettingsNote(
            "Настройка «Всегда поверх окон» сохраняется локально в %LOCALAPPDATA%\\BabyAI\\ui.json."));
        return panel;
    }

    private UIElement BuildDiagnosticsSettings()
    {
        var provider = ReadEnvironment("BABYAI_PROVIDER", "ollama").ToLowerInvariant();
        var panel = CreateSettingsPage(
            "Диагностика",
            "Текущее состояние Core, runtime и desktop bridge.");
        panel.Children.Add(CreateSettingsInfo("Состояние", BrainText.Text));
        panel.Children.Add(CreateSettingsInfo("Провайдер", provider));
        panel.Children.Add(CreateSettingsInfo("Core", CoreStatusText.Text));
        panel.Children.Add(CreateSettingsInfo("Runtime", RuntimeText.Text));
        panel.Children.Add(CreateSettingsInfo("Запуск", StartupText.Text));

        if (provider.Equals("native", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(CreateSettingsInfo(
                "GGUF модель",
                ReadEnvironment("BABYAI_NATIVE_MODEL", "Путь не задан")));
            panel.Children.Add(CreateSettingsInfo(
                "Native runtime",
                ReadEnvironment("BABYAI_NATIVE_RUNTIME", "Путь не задан")));
        }

        panel.Children.Add(CreateSettingsInfo(
            "Управление",
            "Enter — отправить · Shift+Enter — новая строка · Stop — остановить генерацию"));
        return panel;
    }

    private static StackPanel CreateSettingsPage(string title, string subtitle)
    {
        var panel = new StackPanel
        {
            Spacing = 10,
            Padding = new Thickness(2, 0, 8, 8),
        };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
        });
        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Opacity = 0.62,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, -5, 0, 4),
        });
        return panel;
    }

    private static Border CreateSettingsInfo(string label, string value)
    {
        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 9,
            CharacterSpacing = 80,
            Opacity = 0.48,
        });
        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        return CreateSettingsCard(content);
    }

    private static Border CreateSettingsNote(string text)
    {
        return CreateSettingsCard(new TextBlock
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.58,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private static Border CreateSettingsCard(UIElement content)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(11, 9, 11, 9),
            Child = content,
        };
    }
}
