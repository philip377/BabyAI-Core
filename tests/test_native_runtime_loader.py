from __future__ import annotations

import ctypes

import pytest

from babyai.native_runtime import (
    BABYAI_NATIVE_ABI_VERSION,
    REQUIRED_BABYAI_SYMBOLS,
    NativeRuntimeError,
    NativeRuntimeLoader,
)


class _FakeFunction:
    def __init__(self, value=0, callback=None):
        self.value = value
        self.callback = callback
        self.argtypes = None
        self.restype = None

    def __call__(self, *args):
        if self.callback is not None:
            return self.callback(*args)
        return self.value


class _FakeLibrary:
    pass


def _compatible_library(abi_version: int = BABYAI_NATIVE_ABI_VERSION):
    library = _FakeLibrary()
    for name in REQUIRED_BABYAI_SYMBOLS:
        setattr(library, name, _FakeFunction())
    library.babyai_native_abi_version = _FakeFunction(abi_version)
    return library


def _lifecycle_library(*, model_result: int = 0, last_error: bytes = b""):
    calls: list[object] = []
    library = _compatible_library()

    def runtime_create(out_runtime):
        calls.append("runtime_create")
        out_runtime._obj.value = 0x101
        return 0

    def runtime_destroy(runtime):
        calls.append("runtime_destroy")

    def model_open(runtime, path, n_gpu_layers, out_model):
        decoded = path.decode("utf-8")
        calls.append(("model_open", decoded, int(n_gpu_layers)))
        if model_result == 0:
            out_model._obj.value = 0x200 + len(calls)
        return model_result

    def model_close(model):
        calls.append(("model_close", int(model.value or 0)))

    library.babyai_native_runtime_create = _FakeFunction(callback=runtime_create)
    library.babyai_native_runtime_destroy = _FakeFunction(callback=runtime_destroy)
    library.babyai_native_model_open = _FakeFunction(callback=model_open)
    library.babyai_native_model_close = _FakeFunction(callback=model_close)
    library.babyai_native_last_error = _FakeFunction(callback=lambda runtime: last_error)
    return library, calls


def _install_fake_runtime(tmp_path, monkeypatch, library):
    runtime = tmp_path / "babyai_native.dll"
    runtime.write_bytes(b"placeholder")
    monkeypatch.setattr("babyai.native_runtime.ctypes.CDLL", lambda path: library)
    return runtime


def test_loader_rejects_missing_runtime_file(tmp_path):
    loader = NativeRuntimeLoader(tmp_path / "babyai_native.dll")

    with pytest.raises(NativeRuntimeError, match="Native runtime library not found"):
        loader.load()


def test_loader_wraps_dynamic_library_load_error(tmp_path, monkeypatch):
    runtime = tmp_path / "babyai_native.dll"
    runtime.write_bytes(b"placeholder")

    def _fail(path):
        raise OSError("bad image")

    monkeypatch.setattr("babyai.native_runtime.ctypes.CDLL", _fail)

    with pytest.raises(NativeRuntimeError, match="Could not load native runtime library"):
        NativeRuntimeLoader(runtime).load()


def test_loader_rejects_library_missing_required_symbols(tmp_path, monkeypatch):
    runtime = tmp_path / "babyai_native.dll"
    runtime.write_bytes(b"placeholder")
    monkeypatch.setattr("babyai.native_runtime.ctypes.CDLL", lambda path: _FakeLibrary())

    with pytest.raises(NativeRuntimeError, match="does not satisfy BabyAI native ABI v1") as exc:
        NativeRuntimeLoader(runtime).load()

    assert "babyai_native_abi_version" in str(exc.value)
    assert "babyai_native_model_open" in str(exc.value)


def test_loader_rejects_wrong_abi_version(tmp_path, monkeypatch):
    runtime = tmp_path / "babyai_native.dll"
    runtime.write_bytes(b"placeholder")
    monkeypatch.setattr(
        "babyai.native_runtime.ctypes.CDLL",
        lambda path: _compatible_library(abi_version=99),
    )

    with pytest.raises(NativeRuntimeError, match="Native runtime ABI mismatch"):
        NativeRuntimeLoader(runtime).load()


def test_loader_accepts_library_with_required_symbols_and_abi(tmp_path, monkeypatch):
    library = _compatible_library()
    runtime = _install_fake_runtime(tmp_path, monkeypatch, library)

    handle = NativeRuntimeLoader(runtime).load()

    assert handle.path == runtime.resolve()
    assert handle.library is library
    assert handle.abi_version == BABYAI_NATIVE_ABI_VERSION
    assert library.babyai_native_model_open.argtypes == [
        ctypes.c_void_p,
        ctypes.c_char_p,
        ctypes.c_int32,
        ctypes.POINTER(ctypes.c_void_p),
    ]


def test_runtime_context_closes_models_before_backend(tmp_path, monkeypatch):
    library, calls = _lifecycle_library()
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)

    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        first = runtime.open_model(tmp_path / "first.gguf", n_gpu_layers=7)
        second = runtime.open_model(tmp_path / "second.gguf")
        assert not runtime.closed
        assert not first.closed
        assert not second.closed

    assert runtime.closed
    assert first.closed
    assert second.closed
    assert calls[0] == "runtime_create"
    assert calls[1][0] == "model_open"
    assert calls[1][2] == 7
    assert calls[2][0] == "model_open"
    assert calls[-3][0] == "model_close"
    assert calls[-2][0] == "model_close"
    assert calls[-1] == "runtime_destroy"


def test_model_close_is_idempotent_and_not_repeated_by_runtime(tmp_path, monkeypatch):
    library, calls = _lifecycle_library()
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)

    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        model = runtime.open_model(tmp_path / "model.gguf")
        model.close()
        model.close()
        assert model.closed

    model_closes = [call for call in calls if isinstance(call, tuple) and call[0] == "model_close"]
    assert len(model_closes) == 1
    assert calls[-1] == "runtime_destroy"


def test_model_open_error_includes_native_last_error(tmp_path, monkeypatch):
    library, calls = _lifecycle_library(
        model_result=3,
        last_error=b"llama.cpp could not load the configured GGUF model.",
    )
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)

    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        with pytest.raises(NativeRuntimeError, match="configured GGUF model"):
            runtime.open_model(tmp_path / "broken.gguf")

    assert calls[-1] == "runtime_destroy"
