from pathlib import Path


def _desktop_sources() -> tuple[str, str, str, str, str]:
    root = Path(__file__).resolve().parents[1]
    desktop = root / "desktop" / "BabyAI.Desktop"
    return (
        (desktop / "BabyAIBridgeClient.cs").read_text(encoding="utf-8"),
        (desktop / "MainWindow.xaml.cs").read_text(encoding="utf-8"),
        (desktop / "MainWindow.AdaptiveUi.cs").read_text(encoding="utf-8"),
        (desktop / "ReplyActivityBehavior.cs").read_text(encoding="utf-8"),
        (desktop / "ReplyElapsedBehavior.cs").read_text(encoding="utf-8"),
    )


def test_desktop_bridge_requests_v2_and_keeps_v1_without_resending() -> None:
    bridge, _, _, _, _ = _desktop_sources()

    assert "protocol = StreamingProtocolVersion" in bridge
    assert "var status = await StatusAsync(cancellationToken)" in bridge
    assert 'ExecuteStreamingAsync("chat", payload, onEvent, cancellationToken)' in bridge
    assert "if (!hasProtocol && !hasEvent)" in bridge
    assert "ReadLegacyReply(root)" in bridge
    assert "return new DesktopChatResult(reply, legacyMetrics)" in bridge
    assert bridge.count("StandardInput.WriteLineAsync(request)") == 2
    assert 'ExecuteAsync("chat", payload, cancellationToken)' in bridge


def test_desktop_bridge_validates_order_and_awaits_each_stream_event() -> None:
    bridge, _, _, _, _ = _desktop_sources()

    for expected in (
        "ValidateResponseId(root, requestId)",
        "ValidateProtocolVersion(root)",
        'ReadRequiredInt64(root, "seq")',
        "sequence != expectedSequence",
        'case "state"',
        'case "delta"',
        'case "done"',
        'case "error"',
        "await onEvent(streamEvent)",
        "linked.Token.ThrowIfCancellationRequested()",
        "MaxStreamingReplyChars",
        "ResetWorker();",
    ):
        assert expected in bridge

    assert "DesktopChatMetrics" in bridge
    assert "EndToEndTtftMs" in bridge
    assert 'ReadOptionalInt64(metrics, "visible_ttft_ms")' in bridge
    assert 'ReadOptionalInt64(metrics, "native_first_token_ms")' in bridge
    assert 'ReadOptionalInt64(metrics, "generation_ms")' in bridge
    assert 'ReadOptionalInt32(metrics, "generated_tokens")' in bridge
    assert 'ReadOptionalInt32(metrics, "delta_count")' in bridge
    assert 'ReadOptionalInt32(metrics, "model_calls")' in bridge
    assert 'ReadOptionalString(metrics, "stop_reason")' in bridge


def test_desktop_stream_ui_owns_one_turn_and_rejects_ghost_deltas() -> None:
    _, window, adaptive, activity, elapsed = _desktop_sources()

    assert "var chatGeneration = Interlocked.Increment(ref _chatGeneration)" in window
    stop = window.index('ReplyText.Text = "Останавливаю…"')
    invalidate = window.index("Interlocked.Increment(ref _chatGeneration)", stop)
    cancel = window.index("_chatCancellation.Cancel()", stop)
    assert invalidate < cancel

    assert "chatGeneration == Volatile.Read(ref _chatGeneration)" in window
    assert "!chatCancellation.IsCancellationRequested" in window
    assert "responseBuffer.Append(streamEvent.Text)" in window
    assert 'CreateConversationTurn("BabyAI", text)' in window
    assert 'ReplaceConversationTurn(assistantTurnIndex.Value, "BabyAI", text)' in window
    assert "responseBuffer.Clear()" in window
    assert "responseBuffer.Append(result.Reply)" in window
    assert 'AppendConversation("Система", "Остановлено пользователем.")' in window
    assert "if (_busy)" in window

    assert "OrbState.Answering" in window
    assert "BabyAI · отвечаю" in adaptive
    assert 'value.Contains("отвеч")' in activity
    assert 'status.Contains("отвеч")' in elapsed
    assert "Desktop chat first fragment displayed" in window
    assert "первый фрагмент" in window
