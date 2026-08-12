using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace BabyAI.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly BabyAIBridgeClient _bridge = new();
    private OrbState _state = OrbState.Idle;

    public MainWindow()
    {
        InitializeComponent();
        Title = "BabyAI";
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ConfigureWindow();
        ApplyState(OrbState.Idle);
    }

    private void ConfigureWindow()
    {
        AppWindow.Resize(new SizeInt32(132, 132));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
    }

    private async void OrbButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyState(OrbState.Thinking);
            var status = await _bridge.StatusAsync();
            ApplyState(status.RequiresApproval ? OrbState.Approval : OrbState.Idle);
        }
        catch
        {
            ApplyState(OrbState.Error);
        }
    }

    private void ApplyState(OrbState state)
    {
        _state = state;
        StateGlyph.Text = state switch
        {
            OrbState.Idle => "•",
            OrbState.Listening => "≈",
            OrbState.Thinking => "✦",
            OrbState.Approval => "!",
            OrbState.Error => "×",
            _ => "•"
        };
        OrbButton.Opacity = state == OrbState.Thinking ? 0.82 : 1.0;
    }
}

public enum OrbState
{
    Idle,
    Listening,
    Thinking,
    Approval,
    Error
}
