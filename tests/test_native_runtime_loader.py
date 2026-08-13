from __future__ import annotations

import pytest

from babyai.native_runtime import (
    BABYAI_NATIVE_ABI_VERSION,
    REQUIRED_BABYAI_SYMBOLS,
    NativeRuntimeError,
    NativeRuntimeLoader,
)


class _FakeFunction:
    def __init__(self, value=0):
        self.value = value
        self.restype = None

    def __call__(self, *args):
        return self.value


class _FakeLibrary:
    pass


def _compatible_library(abi_version: int = BABYAI_NATIVE_ABI_VERSION):
    library = _FakeLibrary()
    for name in REQUIRED_BABYAI_SYMBOLS:
        setattr(library, name, _FakeFunction())
    library.babyai_native_abi_version = _FakeFunction(abi_version)
    return library


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
    runtime = tmp_path / "babyai_native.dll"
    runtime.write_bytes(b"placeholder")
    library = _compatible_library()
    monkeypatch.setattr("babyai.native_runtime.ctypes.CDLL", lambda path: library)

    handle = NativeRuntimeLoader(runtime).load()

    assert handle.path == runtime.resolve()
    assert handle.library is library
    assert handle.abi_version == BABYAI_NATIVE_ABI_VERSION
