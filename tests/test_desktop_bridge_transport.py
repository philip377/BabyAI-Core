from pathlib import Path


def test_desktop_bridge_transport_contract() -> None:
    root = Path(__file__).resolve().parents[1]
    bridge = (root / "desktop" / "BabyAI.Desktop" / "BabyAIBridgeClient.cs").read_text(encoding="utf-8")

    for text in (
        'ArgumentList.Add("babyai.desktop_worker")',
        "RedirectStandardInput = true",
        "RedirectStandardOutput = true",
        "RedirectStandardError = true",
        "SemaphoreSlim",
        "Interlocked.Increment",
        "StandardInput.WriteLineAsync",
        "StandardOutput.ReadLineAsync",
        'startInfo.Environment["PYTHONUTF8"] = "1"',
        'startInfo.Environment["PYTHONIOENCODING"] = "utf-8"',
        "ResetWorker();",
        "BabyAIBridgeClient : IDisposable",
    ):
        assert text in bridge


def test_brain_indicator_reuses_the_main_window_bridge() -> None:
    root = Path(__file__).resolve().parents[1]
    behavior = (root / "desktop" / "BabyAI.Desktop" / "BrainStatusBehavior.cs").read_text(
        encoding="utf-8"
    )
    window = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.xaml.cs").read_text(
        encoding="utf-8"
    )

    assert "new BabyAIBridgeClient" not in behavior
    assert "ReadDesktopStatusForIndicatorAsync" in behavior
    assert "ReadDesktopStatusForIndicatorAsync" in window
