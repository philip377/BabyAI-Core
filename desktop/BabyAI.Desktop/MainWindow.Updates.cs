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
            _tray.SetUpdateAvailable(update.LatestVersion);
        }
        catch
        {
            // Startup update checks are best-effort and never block BabyAI launch.
        }
    }
}
