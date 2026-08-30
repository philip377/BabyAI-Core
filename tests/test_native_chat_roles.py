from __future__ import annotations

from babyai.native_chat import prepare_native_chat_prompt
from babyai.resident_native_brain import _QWEN_CHAT_STOP_SEQUENCES


def test_native_chat_promotes_recent_episode_to_real_roles() -> None:
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
    assert native.count("<|im_start|>user\n") == 2
    assert native.count("<|im_start|>assistant\n") == 2
    assert "Recent episodic memory:" not in native
    assert (
        "<|im_start|>user\n"
        "привет, расскажи о себе"
        "<|im_end|>"
    ) in native
    assert (
        "<|im_start|>assistant\n"
        "previous English biography"
        "<|im_end|>"
    ) in native
    assert (
        "<|im_start|>user\n"
        "а что на английском то?\n/no_think"
        "<|im_end|>"
    ) in native
    assert native.index("Streaming display contract:") < native.index("<|im_start|>user\n")
    assert native.endswith("<|im_start|>assistant\n\nBABYAI:")


def test_native_chat_keeps_latest_followup_after_previous_russian_answer() -> None:
    prompt = (
        "You are BabyAI.\n\n"
        "Recent episodic memory:\n"
        "USER: давай на русском я на китайском не шарю\n"
        "BABYAI: Привет! Я BabyAI v0.1, это мой первый образ жизни как AI.\n\n"
        "USER: отлично, что расскажешь?"
    )

    native = prepare_native_chat_prompt(prompt)

    old_answer = (
        "<|im_start|>assistant\n"
        "Привет! Я BabyAI v0.1, это мой первый образ жизни как AI."
        "<|im_end|>"
    )
    latest = (
        "<|im_start|>user\n"
        "отлично, что расскажешь?\n/no_think"
        "<|im_end|>"
    )
    assert old_answer in native
    assert latest in native
    assert native.index(old_answer) < native.index(latest)
    assert "Do not repeat an earlier assistant answer unless the user asks you to repeat it." in native


def test_native_chat_preserves_tool_catalog_as_system_context() -> None:
    prompt = (
        "You are BabyAI.\n\n"
        "Recent episodic memory:\n"
        "USER: привет\n"
        "BABYAI: привет!\n\n"
        "Available tools:\n- filesystem.list\n\n"
        "USER: покажи файлы"
    )

    native = prepare_native_chat_prompt(prompt)

    system_end = native.index("<|im_end|>")
    assert "Available tools:\n- filesystem.list" in native[:system_end]
    assert "Available tools:" not in native[system_end + len("<|im_end|>") :]


def test_native_chat_wraps_plain_input_as_user_turn() -> None:
    native = prepare_native_chat_prompt("привет")

    assert "<|im_start|>user\nпривет\n/no_think<|im_end|>" in native
    assert native.endswith("<|im_start|>assistant\n\nBABYAI:")


def test_resident_native_stops_at_qwen_turn_boundaries() -> None:
    assert "<|im_end|>" in _QWEN_CHAT_STOP_SEQUENCES
    assert "<|im_start|>" in _QWEN_CHAT_STOP_SEQUENCES
