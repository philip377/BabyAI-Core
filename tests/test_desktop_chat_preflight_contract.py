from pathlib import Path


def test_desktop_chat_checks_brain_before_starting_generation() -> None:
    root = Path(__file__).resolve().parents[1]
    bridge = (root / "desktop" / "BabyAI.Desktop" / "BabyAIBridgeClient.cs").read_text(encoding="utf-8")

    status_check = bridge.index("var status = await StatusAsync();")
    readiness_gate = bridge.index("if (!status.Brain.Ready)")
    chat_start = bridge.index('ExecuteAsync("chat", payload, cancellationToken)')

    assert status_check < readiness_gate < chat_start
    assert "Brain not ready ({status.Brain.State})" in bridge
    assert "status.Brain.Detail" in bridge
