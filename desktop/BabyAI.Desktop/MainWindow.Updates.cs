using Microsoft.UI.Xaml.Controls;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private int _startupUpdateCheckStarted;

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (Interlocked.Exchange(ref _startupUpdateCheckStarted, 1) != 0)
            return;

        if (!_uiSettings.Load().CheckForUpdatesOnStartup)
            return;

        try
        {
            var update = await BabyAIUpdateService.CheckAsync();
            if (!update.UpdateAvailable)
                return;

            StartupText.Text = $"Доступно обновление {update.LatestVersion}";
            if (Root.XamlRoot is null)
                return;

            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Доступно обновление BabyAI",
                Content = $"Установлена версия {update.CurrentVersion}. Доступна версия {update.LatestVersion}. Откройте Настройки → Общие, чтобы перейти к релизу.",
                CloseButtonText = "Понятно",
                DefaultButton = ContentDialogButton.Close,
            };

            await dialog.ShowAsync();
        }
        catch
        {
            // Startup update checks are best-effort and never block BabyAI launch.
        }
    }
}
