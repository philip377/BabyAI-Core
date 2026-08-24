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
    assert "_bridge.ChatStreamAsync(" in window
    assert "chatCancellation.Token" in window
    assert "_chatCancellation.Cancel()" in window
    assert "Interlocked.Increment(ref _chatGeneration)" in window
    assert 'AppendConversation("Вы", message)' in window
    assert 'CreateConversationTurn("BabyAI", text)' in window
    assert "ReplaceConversationTurn" in window

    assert "CancellationToken cancellationToken = default" in bridge
    assert "Func<DesktopChatEvent, ValueTask> onEvent" in bridge
    assert "CancellationTokenSource.CreateLinkedTokenSource" in bridge
    assert "process.Kill(entireProcessTree: true)" in bridge
    assert "BabyAI response timed out after 3 minutes." in bridge


def test_desktop_chat_keyboard_contract_uses_enter_and_preserves_shift_enter() -> None:
    root = Path(__file__).resolve().parents[1]
    xaml = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.xaml").read_text(encoding="utf-8")
    window = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.xaml.cs").read_text(encoding="utf-8")

    assert 'KeyDown="MessageBox_KeyDown"' in xaml
    assert "e.Key != VirtualKey.Enter" in window
    assert "InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)" in window
    assert "CoreVirtualKeyStates.Down" in window
    assert "e.Handled = true" in window
    assert "MessageBox.Focus(FocusState.Programmatic)" in window
