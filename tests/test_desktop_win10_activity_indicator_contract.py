from pathlib import Path


def test_desktop_avoids_progress_ring_on_win10() -> None:
    root = Path(__file__).resolve().parents[1]
    xaml = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.xaml").read_text(encoding="utf-8")
    app = (root / "desktop" / "BabyAI.Desktop" / "App.xaml.cs").read_text(encoding="utf-8")
    runtime_behaviors = (
        root / "desktop" / "BabyAI.Desktop" / "MainWindow.RuntimeBehaviors.cs"
    ).read_text(encoding="utf-8")
    probe = (root / "desktop" / "BabyAI.Desktop" / "XamlStartupProbe.cs").read_text(
        encoding="utf-8"
    )
    behavior = (
        root / "desktop" / "BabyAI.Desktop" / "ReplyActivityBehavior.cs"
    ).read_text(encoding="utf-8")

    assert "<ProgressRing" not in xaml
    assert 'x:Name="ReplyActivityIndicator"' in xaml
    assert "XamlStartupProbe.Run()" in app
    assert "ProgressRing" not in probe
    assert "XAML-PROBE START" in probe
    assert "SECTION Reply safe indicator" in probe
    assert "ReplyActivityBehavior.SetSource(ReplyActivityIndicator, ReplyText)" in runtime_behaviors
    assert "ConditionalWeakTable<FrameworkElement, Subscription>" in behavior
    assert "indicator.Visibility = active ? Visibility.Visible : Visibility.Collapsed" in behavior
    assert ".IsActive" not in behavior
