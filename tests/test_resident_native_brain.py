from __future__ import annotations

import pytest

from babyai.llm import LLMError
from babyai.native_generation import NativeGenerationResult
from babyai.native_runtime import NativeRuntimeError
from babyai.resident_native_brain import ResidentNativeBrainProvider


class Model:
    def __init__(self, calls):
        self.calls = calls
        self.closed = False

    def close(self):
        if not self.closed:
            self.closed = True
            self.calls.append("model_close")


class Runtime:
    def __init__(self, calls):
        self.calls = calls
        self.closed = False
        self.model = None

    def open_model(self, path, *, n_gpu_layers):
        self.calls.append(("open_model", path, n_gpu_layers))
        self.model = Model(self.calls)
        return self.model

    def close(self):
        if self.closed:
            return
        self.closed = True
        if self.model is not None:
            self.model.close()
        self.calls.append("runtime_close")


class Loader:
    def __init__(self, path, calls):
        self.calls = calls
        self.calls.append(("loader", path))

    def open_runtime(self):
        self.calls.append("open_runtime")
        return Runtime(self.calls)


def result(text="ok"):
    return NativeGenerationResult(
        text=text,
        generated_tokens=1,
        output_bytes=len(text),
        stop_reason="eog",
    )


def test_model_is_loaded_lazily_and_reused(tmp_path, monkeypatch):
    calls = []
    monkeypatch.setattr(
        "babyai.resident_native_brain.NativeRuntimeLoader",
        lambda path: Loader(path, calls),
    )
    monkeypatch.setattr(
        "babyai.resident_native_brain.generate_greedy",
        lambda model, prompt, **kwargs: (calls.append(("generate", model)), result())[1],
    )
    provider = ResidentNativeBrainProvider(
        model_path=tmp_path / "brain.gguf",
        runtime_path=tmp_path / "babyai_native.dll",
        n_gpu_layers=4,
    )

    assert provider.model_is_resident is False
    assert provider.generate("one") == "ok"
    first_model = next(call[1] for call in calls if isinstance(call, tuple) and call[0] == "generate")
    assert provider.generate("two") == "ok"
    generated_models = [call[1] for call in calls if isinstance(call, tuple) and call[0] == "generate"]

    assert generated_models == [first_model, first_model]
    assert sum(call == "open_runtime" for call in calls) == 1
    assert sum(isinstance(call, tuple) and call[0] == "open_model" for call in calls) == 1
    assert ("open_model", tmp_path / "brain.gguf", 4) in calls
    assert provider.model_is_resident is True


def test_close_releases_model_then_runtime(tmp_path, monkeypatch):
    calls = []
    monkeypatch.setattr(
        "babyai.resident_native_brain.NativeRuntimeLoader",
        lambda path: Loader(path, calls),
    )
    monkeypatch.setattr(
        "babyai.resident_native_brain.generate_greedy",
        lambda *args, **kwargs: result(),
    )
    provider = ResidentNativeBrainProvider(
        model_path=tmp_path / "brain.gguf",
        runtime_path=tmp_path / "babyai_native.dll",
    )

    provider.generate("hello")
    provider.close()
    provider.close()

    assert provider.model_is_resident is False
    assert calls[-2:] == ["model_close", "runtime_close"]


def test_native_error_drops_resident_state_and_allows_clean_reload(tmp_path, monkeypatch):
    calls = []
    attempts = 0
    monkeypatch.setattr(
        "babyai.resident_native_brain.NativeRuntimeLoader",
        lambda path: Loader(path, calls),
    )

    def generate(*args, **kwargs):
        nonlocal attempts
        attempts += 1
        if attempts == 1:
            raise NativeRuntimeError("decode failed")
        return result("recovered")

    monkeypatch.setattr("babyai.resident_native_brain.generate_greedy", generate)
    provider = ResidentNativeBrainProvider(
        model_path=tmp_path / "brain.gguf",
        runtime_path=tmp_path / "babyai_native.dll",
    )

    with pytest.raises(LLMError, match="decode failed"):
        provider.generate("first")

    assert provider.model_is_resident is False
    assert provider.generate("second") == "recovered"
    assert sum(call == "open_runtime" for call in calls) == 2
