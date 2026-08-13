from pathlib import Path


def test_startup_summary_uses_existing_brain_status() -> None:
    root = Path(__file__).resolve().parents[1]
    xaml = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.xaml").read_text(encoding="utf-8")
    behavior = (root / "desktop" / "BabyAI.Desktop" / "BrainStatusBehavior.cs").read_text(encoding="utf-8")
    window = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.BrainStatus.cs").read_text(encoding="utf-8")

    assert 'x:Name="StartupText"' in xaml
    assert "ApplyBrainReadinessFromIndicator(status.Brain)" in behavior
    assert "ApplyStartupFailureFromIndicator()" in behavior
    assert "FormatStartupReadiness" in window
    assert '"ready"' in window
    assert '"unavailable"' in window
    assert '"model_missing"' in window
    assert "11434" not in window
    assert "/api/tags" not in window
    assert "Process.Start" not in window
