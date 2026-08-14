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
            _uiSettings.Save(new DesktopUiSettings(current.AlwaysOnTop, automaticCheck.IsOn));
        };
        panel.Children.Add(CreateSettingsCard(automaticCheck));

        BabyAIUpdateInfo? availableUpdate = null;
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
        var installButton = new Button
        {
            Content = "Скачать и установить",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 5, 0, 0),
            IsEnabled = false,
        };

        checkButton.Click += async (_, _) =>
        {
            checkButton.IsEnabled = false;
            installButton.IsEnabled = false;
            availableUpdate = null;
            status.Text = "Проверяем обновления…";
            try
            {
                var update = await BabyAIUpdateService.CheckAsync();
                if (update.UpdateAvailable)
                {
                    availableUpdate = update;
                    status.Text = update.DownloadAvailable
                        ? $"Доступна версия {update.LatestVersion}. Установщик будет проверен по SHA-256 перед запуском."
                        : $"Доступна версия {update.LatestVersion}; Windows installer пока не опубликован.";
                    installButton.IsEnabled = update.DownloadAvailable;
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
                status.Text = "Не удалось проверить обновления. Попробуйте ещё раз позже.";
            }
            finally
            {
                checkButton.IsEnabled = true;
            }
        };

        installButton.Click += async (_, _) =>
        {
            if (availableUpdate is null || !availableUpdate.DownloadAvailable)
                return;

            checkButton.IsEnabled = false;
            installButton.IsEnabled = false;
            status.Text = "Скачиваем фирменный установщик и проверяем SHA-256…";
            try
            {
                var ready = await BabyAIUpdateService.DownloadVerifiedAsync(availableUpdate);
                status.Text = $"BabyAI {ready.Version} проверен. Запускаем обновление…";
                BabyAIUpdateService.LaunchInstaller(ready);
            }
            catch
            {
                status.Text = "Не удалось подготовить или запустить обновление.";
                installButton.IsEnabled = true;
            }
            finally
            {
                checkButton.IsEnabled = true;
            }
        };

        var card = new StackPanel { Spacing = 2 };
        card.Children.Add(status);
        card.Children.Add(checkButton);
        card.Children.Add(installButton);
        panel.Children.Add(CreateSettingsCard(card));
        panel.Children.Add(CreateSettingsNote(
            "Обновление скачивает только фирменный BabyAI Setup с GitHub Releases, проверяет SHA-256 и затем передаёт установку существующему atomic/rollback-safe установщику."));
        return panel;
    }
}
