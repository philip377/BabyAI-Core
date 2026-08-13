using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private UIElement BuildUpdateSettings()
    {
        var stored = _uiSettings.Load();
        var panel = CreateSettingsPage(
            "Обновления",
            "Проверка новых версий BabyAI через GitHub Releases.");

        panel.Children.Add(CreateSettingsInfo("Текущая версия", BabyAIUpdateService.CurrentVersionText));

        var automaticCheck = new ToggleSwitch
        {
            Header = "Проверять обновления при запуске",
            IsOn = stored.CheckForUpdatesOnStartup,
            OnContent = "Включено",
            OffContent = "Выключено",
            Margin = new Thickness(2, 2, 2, 4),
        };
        automaticCheck.Toggled += (_, _) =>
        {
            var current = _uiSettings.Load();
            _uiSettings.Save(new DesktopUiSettings(
                current.AlwaysOnTop,
                automaticCheck.IsOn));
        };
        panel.Children.Add(CreateSettingsCard(automaticCheck));

        var status = new TextBlock
        {
            Text = "Нажмите «Проверить сейчас», чтобы проверить GitHub Releases.",
            FontSize = 11,
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
        };
        var checkButton = new Button
        {
            Content = "Проверить сейчас",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 7, 0, 0),
        };
        checkButton.Click += async (_, _) =>
        {
            checkButton.IsEnabled = false;
            status.Text = "Проверяем обновления…";
            try
            {
                var update = await BabyAIUpdateService.CheckAsync();
                if (update.UpdateAvailable)
                {
                    status.Text = $"Доступна версия {update.LatestVersion}. Страница релиза: {update.ReleaseUrl}";
                }
                else if (string.IsNullOrWhiteSpace(update.LatestVersion))
                {
                    status.Text = "Публичные релизы BabyAI на GitHub пока не опубликованы.";
                }
                else
                {
                    status.Text = $"Установлена актуальная версия. Последний релиз: {update.LatestVersion}.";
                }
            }
            catch
            {
                status.Text = "Не удалось проверить обновления. Проверьте подключение к интернету и попробуйте ещё раз.";
            }
            finally
            {
                checkButton.IsEnabled = true;
            }
        };

        var card = new StackPanel { Spacing = 2 };
        card.Children.Add(status);
        card.Children.Add(checkButton);
        panel.Children.Add(CreateSettingsCard(card));
        panel.Children.Add(CreateSettingsNote(
            "Автопроверка только читает информацию о последнем GitHub Release. BabyAI ничего не скачивает и не устанавливает без вашего действия."));
        return panel;
    }
}
