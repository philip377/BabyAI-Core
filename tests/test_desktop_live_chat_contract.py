from pathlib import Path


def test_desktop_live_chat_exposes_transcript_runtime_and_stop_controls() -> None:
    root = Path(__file__).resolve().parents[1]
    xaml = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.xaml").read_text(encoding="utf-8")
    window = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.xaml.cs").read_text(encoding="utf-8")
    bridge = (root / "desktop" / "BabyAI.Desktop" / "BabyAIBridgeClient.cs").read_text(encoding="utf-8")

    assert 'x:Name="ConversationText"' in xaml
    assert 'x:Name="ConversationScroller"' in xaml
    assert 'x:Name="RuntimeText"' in xaml
    assert 'x:Name="StopButton"' in xaml

    assert "CancellationTokenSource? _chatCancellation" in window
    assert '_bridge.ChatAsync(message, _chatCancellation.Token)' in window
    assert "_chatCancellation.Cancel()" in window
    assert 'AppendConversation("You", message)' in window
    assert 'AppendConversation("BabyAI", reply)' in window

    assert "CancellationToken cancellationToken = default" in bridge
    assert "CancellationTokenSource.CreateLinkedTokenSource" in bridge
    assert "process.Kill(entireProcessTree: true)" in bridge
    assert "BabyAI response timed out after 3 minutes." in bridge
