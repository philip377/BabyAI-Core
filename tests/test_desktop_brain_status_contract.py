from pathlib import Path


def test_desktop_bridge_exposes_brain_readiness_to_winui() -> None:
    root = Path(__file__).resolve().parents[1]
    bridge = (root / "desktop" / "BabyAI.Desktop" / "BabyAIBridgeClient.cs").read_text(encoding="utf-8")

    assert 'var runtime = snapshot.GetProperty("runtime")' in bridge
    assert "new BrainStatus(" in bridge
    assert "runtime.GetProperty(\"state\")" in bridge
    assert "runtime.GetProperty(\"ready\").GetBoolean()" in bridge
    assert "public sealed record BrainStatus(" in bridge
    assert "BrainStatus Brain" in bridge
