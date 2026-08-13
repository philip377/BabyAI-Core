from __future__ import annotations

import ctypes

import pytest

from babyai.native_backend import inspect_native_acceleration
from babyai.native_runtime import BABYAI_NATIVE_ABI_VERSION, NativeRuntimeError


class Fn:
    def __init__(self, callback):
        self.callback = callback
        self.argtypes = None
        self.restype = None

    def __call__(self, *args):
        return self.callback(*args)


class Lib:
    def __init__(self, *, gpu=None, create_result=0):
        self.babyai_native_abi_version = Fn(lambda: BABYAI_NATIVE_ABI_VERSION)
        self.babyai_native_build_backend = Fn(lambda: b"vulkan")
        self.babyai_native_runtime_create = Fn(
            lambda out: self._create(out, create_result)
        )
        self.babyai_native_runtime_destroy = Fn(lambda runtime: None)
        if gpu is not None:
            self.babyai_native_runtime_gpu_available = Fn(lambda runtime: int(gpu))

    @staticmethod
    def _create(out, result):
        if result == 0:
            ctypes.cast(out, ctypes.POINTER(ctypes.c_void_p))[0] = ctypes.c_void_p(1234)
        return result


def install(tmp_path, monkeypatch, library):
    path = tmp_path / "babyai_native.dll"
    path.write_bytes(b"x")
    monkeypatch.setattr("babyai.native_backend.ctypes.CDLL", lambda _: library)
    return path


def test_old_v6_runtime_without_gpu_probe_is_conservative(tmp_path, monkeypatch):
    info = inspect_native_acceleration(install(tmp_path, monkeypatch, Lib()))
    assert info.gpu_probe_available is False
    assert info.gpu_available is False


@pytest.mark.parametrize("available", [False, True])
def test_gpu_probe_reports_runtime_capability(tmp_path, monkeypatch, available):
    info = inspect_native_acceleration(install(tmp_path, monkeypatch, Lib(gpu=available)))
    assert info.backend.build_backend == "vulkan"
    assert info.gpu_probe_available is True
    assert info.gpu_available is available


def test_gpu_probe_fails_if_runtime_cannot_initialize(tmp_path, monkeypatch):
    path = install(tmp_path, monkeypatch, Lib(gpu=True, create_result=4))
    with pytest.raises(NativeRuntimeError, match="could not initialize"):
        inspect_native_acceleration(path)
