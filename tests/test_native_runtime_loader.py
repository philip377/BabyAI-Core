from __future__ import annotations

import pytest

from babyai.native_runtime import (
    REQUIRED_LLAMA_SYMBOLS,
    NativeRuntimeError,
    NativeRuntimeLoader,
)


class _FakeLibrary:
    pass


def _compatible_library():
    library = _FakeLibrary()
    for name in REQUIRED_LLAMA_SYMBOLS:
        setattr(library, name, object())
    return library


def test_loader_rejects_missing_runtime_file(tmp_path):
    loader = NativeRuntimeLoader(tmp_path / "llama.dll")

    with pytest.raises(NativeRuntimeError, match="Native runtime library not found"):
        loader.load()


def test_loader_wraps_dynamic_library_load_error(tmp_path, monkeypatch):
    runtime = tmp_path / "llama.dll"
    runtime.write_bytes(b"placeholder")

    def _fail(path):
        raise OSError("bad image")

    monkeypatch.setattr("babyai.native_runtime.ctypes.CDLL", _fail)

    with pytest.raises(NativeRuntimeError, match="Could not load native runtime library"):
        NativeRuntimeLoader(runtime).load()


def test_loader_rejects_library_missing_required_symbols(tmp_path, monkeypatch):
    runtime = tmp_path / "llama.dll"
    runtime.write_bytes(b"placeholder")
    monkeypatch.setattr("babyai.native_runtime.ctypes.CDLL", lambda path: _FakeLibrary())

    with pytest.raises(NativeRuntimeError, match="does not satisfy BabyAI llama.cpp ABI v1") as exc:
        NativeRuntimeLoader(runtime).load()

    assert "llama_backend_init" in str(exc.value)
    assert "llama_model_load_from_file" in str(exc.value)


def test_loader_accepts_library_with_required_symbols(tmp_path, monkeypatch):
    runtime = tmp_path / "llama.dll"
    runtime.write_bytes(b"placeholder")
    library = _compatible_library()
    monkeypatch.setattr("babyai.native_runtime.ctypes.CDLL", lambda path: library)

    handle = NativeRuntimeLoader(runtime).load()

    assert handle.path == runtime.resolve()
    assert handle.library is library
