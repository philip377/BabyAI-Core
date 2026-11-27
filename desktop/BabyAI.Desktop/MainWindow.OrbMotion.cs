using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace BabyAI.Desktop;

public sealed partial class MainWindow
{
    private Storyboard? _orbMotion;
    private UISettings? _orbMotionSettings;
    private RotateTransform? _orbRingRotation;
    private OrbState? _orbMotionState;
    private bool _orbMotionInitialized;
    private bool _orbMotionDisposed;
    private bool _orbTerminalMotionPlayed;
    private bool _orbAnimationSettingsSubscribed;

    // One subscription set for the window lifetime. No timers or per-frame UI
    // callbacks: transform/opacity clocks can run on the compositor thread.
    private void InitializeOrbMotion()
    {
        if (_orbMotionInitialized || _orbMotionDisposed) return;
        _orbMotionInitialized = true;
        _orbRingRotation = new RotateTransform();
        OrbRing.RenderTransformOrigin = new Point(0.5, 0.5);
        OrbRing.RenderTransform = _orbRingRotation;
        OrbArtwork.Loaded += OrbMotionLoaded;
        OrbArtwork.Unloaded += OrbMotionUnloaded;
        AppWindow.Changed += OrbMotionWindowChanged;
        Closed += OrbMotionClosed;
        try
        {
            _orbMotionSettings = new UISettings();
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                _orbMotionSettings.AnimationsEnabledChanged += OrbAnimationsEnabledChanged;
                _orbAnimationSettingsSubscribed = true;
            }
        }
        catch (Exception ex)
        {
            // A static, readable state is the fallback when settings are unavailable.
            _orbMotionSettings = null;
            StartupDiagnostics.Log("Orb motion settings unavailable", ex);
        }
    }

    private void ApplyState(OrbState state)
    {
        if (_orbMotionDisposed) return;
        InitializeOrbMotion();
        var changed = _orbMotionState != state;
        if (changed)
        {
            StopOrbMotion();
            _orbTerminalMotionPlayed = false;
        }
        _state = state;
        _orbMotionState = state;
        StateGlyph.Text = state switch
        {
            OrbState.Listening => "≈",
            OrbState.Thinking => "✦",
            OrbState.Answering => "…",
            OrbState.Executing => "›",
            OrbState.Approval => "!",
            OrbState.Done => "✓",
            OrbState.Error => "×",
            _ => "•"
        };
        var appearance = OrbAppearance.For(state);
        MidStop.Color = appearance.Middle;
        EdgeStop.Color = appearance.Edge;
        OrbGlow.Fill = new SolidColorBrush(appearance.GlowColor);
        OrbRing.Stroke = new SolidColorBrush(Color.FromArgb(190,
            appearance.GlowColor.R, appearance.GlowColor.G, appearance.GlowColor.B));
        // Keep the small header glyph legible against the illuminated core.
        StateGlyph.Foreground = new SolidColorBrush(Color.FromArgb(240, 22, 30, 52));
        OrbButton.Opacity = 1;
        if (changed)
        {
            // Interrupted arcs signal activity without suggesting percentage
            // completion. The glyph and outer button never rotate or shift.
            OrbRing.StrokeDashArray = appearance.OrbitMilliseconds > 0
                ? new DoubleCollection { 6, 15, 2, 15 } : new DoubleCollection();
            SetStaticOrbAppearance();
        }
        RefreshOrbMotionVisibility();
    }

    private void RefreshOrbMotionVisibility()
    {
        if (!_orbMotionInitialized || _orbMotionDisposed) return;
        var visible = Root.IsLoaded && OrbArtwork.IsLoaded && AppWindow.IsVisible && !_exitRequested &&
            OrbArtwork.Visibility == Visibility.Visible &&
            (AppWindow.Presenter is not OverlappedPresenter presenter ||
             presenter.State != OverlappedPresenterState.Minimized);
        var enabled = _orbMotionSettings?.AnimationsEnabled == true;
        if (!visible || !enabled)
        {
            StopOrbMotion();
            return;
        }
        if (_orbMotion is not null) return; // Repeated status updates retain phase.
        var appearance = OrbAppearance.For(_state);
        if (!appearance.Repeats && _orbTerminalMotionPlayed) return;

        var motion = new Storyboard();
        AddOrbPulse(motion, OrbScale, "ScaleX", 1, appearance.PeakScale, appearance);
        AddOrbPulse(motion, OrbScale, "ScaleY", 1, appearance.PeakScale, appearance);
        AddOrbPulse(motion, OrbGlow, "Opacity", appearance.RestGlow, appearance.PeakGlow, appearance);
        if (appearance.OrbitMilliseconds > 0 && _orbRingRotation is not null)
        {
            var orbit = new DoubleAnimation
            {
                From = 0, To = 360,
                Duration = new Duration(TimeSpan.FromMilliseconds(appearance.OrbitMilliseconds)),
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(orbit, _orbRingRotation);
            Storyboard.SetTargetProperty(orbit, "Angle");
            motion.Children.Add(orbit);
        }
        _orbMotion = motion;
        if (!appearance.Repeats) _orbTerminalMotionPlayed = true;
        motion.Begin();
    }

    private static void AddOrbPulse(Storyboard storyboard, DependencyObject target, string property,
        double start, double peak, OrbAppearance appearance)
    {
        var pulse = new DoubleAnimation
        {
            From = start, To = peak,
            Duration = new Duration(TimeSpan.FromMilliseconds(appearance.PulseMilliseconds)),
            AutoReverse = true,
            RepeatBehavior = appearance.Repeats ? RepeatBehavior.Forever : new RepeatBehavior(1),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(pulse, target);
        Storyboard.SetTargetProperty(pulse, property);
        storyboard.Children.Add(pulse);
    }

    private void StopOrbMotion()
    {
        _orbMotion?.Stop();
        _orbMotion = null;
        SetStaticOrbAppearance();
    }

    private void SetStaticOrbAppearance()
    {
        var appearance = OrbAppearance.For(_state);
        OrbScale.ScaleX = OrbScale.ScaleY = 1;
        if (_orbRingRotation is not null) _orbRingRotation.Angle = 0;
        OrbGlow.Opacity = appearance.RestGlow;
        OrbRing.Opacity = _state is OrbState.Thinking or OrbState.Executing ? 0.75 : 0.48;
    }

    private void OrbMotionLoaded(object sender, RoutedEventArgs args) => RefreshOrbMotionVisibility();
    private void OrbMotionUnloaded(object sender, RoutedEventArgs args) => StopOrbMotion();
    private void OrbMotionWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => RefreshOrbMotionVisibility();
    private void OrbAnimationsEnabledChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_orbMotionDisposed) RefreshOrbMotionVisibility();
        });

    private void OrbMotionClosed(object sender, WindowEventArgs args)
    {
        if (_orbMotionDisposed) return;
        _orbMotionDisposed = true;
        StopOrbMotion();
        OrbArtwork.Loaded -= OrbMotionLoaded;
        OrbArtwork.Unloaded -= OrbMotionUnloaded;
        AppWindow.Changed -= OrbMotionWindowChanged;
        Closed -= OrbMotionClosed;
        if (_orbMotionSettings is not null && _orbAnimationSettingsSubscribed &&
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _orbMotionSettings.AnimationsEnabledChanged -= OrbAnimationsEnabledChanged;
        _orbAnimationSettingsSubscribed = false;
        _orbMotionSettings = null;
    }

    private sealed record OrbAppearance(double PeakScale, double RestGlow, double PeakGlow,
        int PulseMilliseconds, int OrbitMilliseconds, bool Repeats, Color GlowColor, Color Middle, Color Edge)
    {
        internal static OrbAppearance For(OrbState state) => state switch
        {
            OrbState.Listening => new(1.048, 0.29, 0.48, 900, 0, true,
                Rgb(99, 220, 255), Rgb(148, 228, 255), Rgb(65, 151, 255)),
            OrbState.Thinking => new(1.035, 0.27, 0.43, 1700, 6800, true,
                Rgb(174, 122, 255), Rgb(183, 157, 255), Rgb(111, 90, 255)),
            OrbState.Answering => new(1.025, 0.27, 0.40, 1250, 0, true,
                Rgb(125, 164, 255), Rgb(173, 195, 255), Rgb(79, 112, 229)),
            OrbState.Executing => new(1.028, 0.28, 0.45, 1200, 3800, true,
                Rgb(83, 205, 214), Rgb(144, 229, 233), Rgb(45, 156, 171)),
            OrbState.Approval => new(1.016, 0.32, 0.44, 2400, 0, true,
                Rgb(255, 190, 86), Rgb(255, 213, 144), Rgb(227, 148, 44)),
            OrbState.Done => new(1.035, 0.30, 0.43, 450, 0, false,
                Rgb(76, 212, 145), Rgb(145, 237, 188), Rgb(49, 165, 112)),
            // One small acknowledgement, then a static error; no red flashing.
            OrbState.Error => new(1.016, 0.32, 0.39, 650, 0, false,
                Rgb(255, 96, 113), Rgb(255, 160, 170), Rgb(208, 62, 84)),
            _ => new(1.018, 0.22, 0.30, 3400, 0, true,
                Rgb(124, 141, 255), Rgb(184, 200, 255), Rgb(109, 124, 255))
        };

        private static Color Rgb(byte red, byte green, byte blue) => Color.FromArgb(255, red, green, blue);
    }
}
