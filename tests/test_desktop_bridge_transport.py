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
        "ResetWorker();",
        "BabyAIBridgeClient : IDisposable",
    ):
        assert text in bridge
