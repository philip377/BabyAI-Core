using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private void SetShellExpanded(bool expanded)
    {
        // Reuse the same orb and its state: no second surface, empty rail, or hidden overlay.
        var host = expanded ? HeaderOrbHost : OrbDock;
        if (!ReferenceEquals(OrbButton.Parent, host))
        {
            if (OrbButton.Parent is Panel previous) previous.Children.Remove(OrbButton);
            host.Children.Add(OrbButton);
        }
        var diameter = expanded ? 40 : 112;
        OrbButton.Width = OrbButton.Height = diameter;
        OrbViewbox.Width = OrbViewbox.Height = diameter;
        OrbButton.CornerRadius = new CornerRadius(diameter / 2);
        OrbDock.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        UpdateOrbPresence();
    }

    private void WindowDragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(WindowDragHandle).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        BeginWindowDrag();
    }

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) => MinimizeWindow();
    private void WindowMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximizeWindow();
    private void WindowHide_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void ConfigureApplicationIcon()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Unix.ico");
            if (File.Exists(icon)) AppWindow.SetIcon(icon);
        }
        catch (Exception ex) { StartupDiagnostics.Log("Window icon could not be loaded", ex); }
    }
}
