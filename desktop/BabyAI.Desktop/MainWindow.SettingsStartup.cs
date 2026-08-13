namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    internal void ApplyStartupUiSettings()
    {
        ApplyStoredUiSettings();
        Root.Loaded -= Root_Loaded;
        Root.Loaded += Root_Loaded;
    }
}
