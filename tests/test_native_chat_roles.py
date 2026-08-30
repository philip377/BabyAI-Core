from __future__ import annotations

from babyai.native_chat import prepare_native_chat_prompt
from babyai.resident_native_brain import _QWEN_CHAT_STOP_SEQUENCES


def test_native_chat_promotes_only_latest_top_level_user_turn() -> None:
    prompt = (
        "You are BabyAI. Respond in the user's latest language.\n\n"
        "Recent episodic memory:\n"
        "USER: привет, расскажи о себе\n"
        "BABYAI: previous English biography\n\n"
        "USER: а что на английском то?\n\n"
        "Streaming display contract: begin with <babyai-visible-0123456789abcdef0123456789abcdef>."
    )

    native = prepare_native_chat_prompt(prompt)

    assert native.startswith("<|im_start|>system\n")
    assert native.count("<|im_start|>user\n") == 1
    assert "Recent episodic memory:\nUSER: привет, расскажи о себе" in native
    assert "BABYAI: previous English biography" in native
    assert (
        "<|im_start|>user\n"
        "а что на английском то?\n/no_think"
        "<|im_end|>"
    ) in native
    assert native.index("Streaming display contract:") < native.index("<|im_start|>user\n")
    assert native.endswith("<|im_start|>assistant\n\nBABYAI:")


def test_native_chat_wraps_plain_input_as_user_turn() -> None:
    native = prepare_native_chat_prompt("привет")

    assert "<|im_start|>user\nпривет\n/no_think<|im_end|>" in native
    assert native.endswith("<|im_start|>assistant\n\nBABYAI:")


def test_resident_native_stops_at_qwen_turn_boundaries() -> None:
    assert "<|im_end|>" in _QWEN_CHAT_STOP_SEQUENCES
    assert "<|im_start|>" in _QWEN_CHAT_STOP_SEQUENCES
