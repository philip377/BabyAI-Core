from __future__ import annotations

import pytest

from babyai.native_backend import inspect_native_backend
from babyai.native_runtime import BABYAI_NATIVE_ABI_VERSION, NativeRuntimeError


class Fn:
    def __init__(self, value):
        self.value = value
        self.argtypes = None
        self.restype = None

    def __call__(self):
        return self.value


class Lib:
    def __init__(self, backend=None, abi=BABYAI_NATIVE_ABI_VERSION):
        self.babyai_native_abi_version = Fn(abi)
        if backend is not None:
            self.babyai_native_build_backend = Fn(backend)


def install(tmp_path, monkeypatch, library):
    path = tmp_path / "babyai_native.dll"
    path.write_bytes(b"x")
    monkeypatch.setattr("babyai.native_backend.ctypes.CDLL", lambda _: library)
    return path


def test_old_v6_dll_reports_unknown(tmp_path, monkeypatch):
    info = inspect_native_backend(install(tmp_path, monkeypatch, Lib()))
    assert info.build_backend == "unknown"
    assert info.metadata_available is False


@pytest.mark.parametrize(("raw", "expected"), [(b"cpu", "cpu"), (b"vulkan", "vulkan")])
def test_known_backend_metadata(tmp_path, monkeypatch, raw, expected):
    info = inspect_native_backend(install(tmp_path, monkeypatch, Lib(raw)))
    assert info.build_backend == expected
    assert info.metadata_available is True


def test_unknown_backend_fails_closed(tmp_path, monkeypatch):
    path = install(tmp_path, monkeypatch, Lib(b"other"))
    with pytest.raises(NativeRuntimeError, match="unsupported build backend"):
        inspect_native_backend(path)


def test_wrong_abi_is_rejected(tmp_path, monkeypatch):
    path = install(tmp_path, monkeypatch, Lib(b"cpu", abi=99))
    with pytest.raises(NativeRuntimeError, match="ABI mismatch"):
        inspect_native_backend(path)
