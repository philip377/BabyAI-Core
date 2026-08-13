using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    internal void ApplyGlassUi()
    {
        // Keep the acrylic backdrop visible instead of covering it with an almost
        // opaque panel. The inner cards remain translucent and readable.
        Panel.Background = new SolidColorBrush(Color.FromArgb(176, 20, 23, 32));
        Panel.BorderBrush = new SolidColorBrush(Color.FromArgb(72, 255, 255, 255));
    }
}
