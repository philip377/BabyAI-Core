from __future__ import annotations

import pytest

from babyai.brain import probe_brain_runtime
from babyai.config import BabyAIConfig
from babyai.llm import LLMError
from babyai.native_brain import NativeBrainProvider, _normalise_native_reply
from babyai.native_generation import NativeGenerationResult
from babyai.native_runtime import NativeRuntimeError


class _FakeModel:
    def __init__(self, calls):
        self.calls = calls

    def __enter__(self):
        self.calls.append("model_enter")
        return self

    def __exit__(self, exc_type, exc, tb):
        self.calls.append("model_exit")


class _FakeRuntime:
    def __init__(self, calls):
        self.calls = calls

    def __enter__(self):
        self.calls.append("runtime_enter")
        return self

    def __exit__(self, exc_type, exc, tb):
        self.calls.append("runtime_exit")

    def open_model(self, path, *, n_gpu_layers):
        self.calls.append(("open_model", path, n_gpu_layers))
        return _FakeModel(self.calls)


class _FakeLoader:
    def __init__(self, path, calls):
        self.path = path
        self.calls = calls
        self.calls.append(("loader", path))

    def open_runtime(self):
        self.calls.append("open_runtime")
        return _FakeRuntime(self.calls)


def test_native_provider_runs_bounded_generation_and_closes_native_lifetime(tmp_path, monkeypatch):
    calls = []
    model_path = tmp_path / "brain.gguf"
    runtime_path = tmp_path / "babyai_native.dll"

    monkeypatch.setattr(
        "babyai.native_brain.NativeRuntimeLoader",
        lambda path: _FakeLoader(path, calls),
    )

    def fake_generate(model, prompt, **kwargs):
        calls.append(("generate", model, prompt, kwargs))
        return NativeGenerationResult(
            text="  Привет из GGUF  ",
            generated_tokens=7,
            output_bytes=24,
            stop_reason="eog",
        )

    monkeypatch.setattr("babyai.native_brain.generate_greedy", fake_generate)
    provider = NativeBrainProvider(
        model_path=model_path,
        runtime_path=runtime_path,
        max_tokens=77,
        max_output_bytes=12345,
        n_ctx=2048,
        n_batch=1024,
        n_threads=6,
        n_gpu_layers=3,
    )

    assert provider.generate("hello") == "Привет из GGUF"
    assert calls[0] == ("loader", runtime_path)
    assert ("open_model", model_path, 3) in calls
    generate_call = next(call for call in calls if isinstance(call, tuple) and call[0] == "generate")
    assert generate_call[2].startswith("hello")
    assert "/no_think" in generate_call[2]
    assert generate_call[2].endswith("BABYAI:")
    assert generate_call[3] == {
        "max_tokens": 77,
        "max_output_bytes": 12345,
        "n_ctx": 2048,
        "n_batch": 1024,
        "n_threads": 6,
        "stop_sequences": ("\n\nUSER:", "\nUSER:", "\n\nBABYAI:", "\nBABYAI:"),
    }
    assert calls[-2:] == ["model_exit", "runtime_exit"]


def test_native_provider_defaults_to_shorter_first_chat_turn(tmp_path):
    provider = NativeBrainProvider(
        model_path=tmp_path / "brain.gguf",
        runtime_path=tmp_path / "babyai_native.dll",
    )

    assert provider.max_tokens == 128


def test_native_reply_extracts_final_response_wrapper_after_reasoning():
    raw = (
        "BABYAI: No tools needed.\n"
        "Long internal-looking explanation.\n"
        "```json\n{\"response\": \"Привет! Чем помочь?\"}\n```"
    )

    assert _normalise_native_reply(raw) == "Привет! Чем помочь?"


def test_native_reply_strips_think_block_and_preserves_tool_json():
    raw = '<think>private scratch</think>\n```json\n{"tool":"system.info","arguments":{}}\n```'

    assert _normalise_native_reply(raw) == '{"tool":"system.info","arguments":{}}'


def test_native_provider_translates_native_runtime_error_to_llm_error(tmp_path, monkeypatch):
    class FailingLoader:
        def __init__(self, path):
            pass

        def open_runtime(self):
            raise NativeRuntimeError("ABI mismatch")

    monkeypatch.setattr("babyai.native_brain.NativeRuntimeLoader", FailingLoader)
    provider = NativeBrainProvider(
        model_path=tmp_path / "brain.gguf",
        runtime_path=tmp_path / "babyai_native.dll",
    )

    with pytest.raises(LLMError, match="Native brain inference failed: ABI mismatch"):
        provider.generate("hello")


def test_native_provider_rejects_empty_generation_result(tmp_path, monkeypatch):
    calls = []
    monkeypatch.setattr(
        "babyai.native_brain.NativeRuntimeLoader",
        lambda path: _FakeLoader(path, calls),
    )
    monkeypatch.setattr(
        "babyai.native_brain.generate_greedy",
        lambda *args, **kwargs: NativeGenerationResult(
            text="   ", generated_tokens=0, output_bytes=0, stop_reason="eog"
        ),
    )
    provider = NativeBrainProvider(
        model_path=tmp_path / "brain.gguf",
        runtime_path=tmp_path / "babyai_native.dll",
    )

    with pytest.raises(LLMError, match="Native brain returned no text"):
        provider.generate("hello")


def test_native_readiness_allows_chat_when_model_and_runtime_files_are_present(tmp_path):
    model_path = tmp_path / "brain.gguf"
    runtime_path = tmp_path / "babyai_native.dll"
    model_path.write_bytes(b"gguf-placeholder")
    runtime_path.write_bytes(b"dll-placeholder")
    config = BabyAIConfig(
        data_dir=tmp_path,
        provider="native",
        native_model_path=model_path,
        native_runtime_path=runtime_path,
    )

    status = probe_brain_runtime(config)

    assert status.provider == "native"
    assert status.state == "ready"
    assert status.ready is True
    assert "validated when generation is explicitly requested" in status.detail
