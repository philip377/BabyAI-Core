from pathlib import Path


def test_desktop_brain_indicator_uses_core_readiness_snapshot() -> None:
    root = Path(__file__).resolve().parents[1]
    xaml = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.xaml").read_text(encoding="utf-8")
    app = (root / "desktop" / "BabyAI.Desktop" / "App.xaml.cs").read_text(encoding="utf-8")
    behavior = (root / "desktop" / "BabyAI.Desktop" / "BrainStatusBehavior.cs").read_text(encoding="utf-8")
    window = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.BrainStatus.cs").read_text(encoding="utf-8")

    assert 'x:Name="BrainText"' in xaml
    assert 'local:BrainStatusBehavior.Enabled="True"' in xaml
    assert "internal MainWindow? MainWindow" in app

    assert "Bridge.StatusAsync()" in behavior
    assert "status.Brain" in behavior
    assert '"model_missing"' in behavior
    assert '"unavailable"' in behavior
    assert "start Ollama" in behavior
    assert "ollama pull {brain.Model}" in behavior
    assert "Install the configured model manually" in behavior
    assert "DispatcherTimer" in behavior
    assert "TimeSpan.FromSeconds(15)" in behavior
    assert "RecoveryTimer.Start()" in behavior
    assert "RecoveryTimer.Stop()" in behavior
    assert "text.Unloaded += OnUnloaded" in behavior
    assert "unsupported_provider" in behavior
    assert "11434" not in behavior
    assert "/api/tags" not in behavior
    assert "Process.Start" not in behavior

    assert "ApplyBrainReadinessFromIndicator" in window
    assert "brain.Ready ? OrbState.Idle : OrbState.Error" in window
    assert "OrbState.Approval" in window
    assert "OrbState.Thinking" in window
