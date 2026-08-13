from __future__ import annotations

import ctypes
import os
from contextlib import nullcontext
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


BABYAI_NATIVE_ABI_VERSION = 1
BABYAI_NATIVE_OK = 0


class NativeRuntimeError(RuntimeError):
    """Raised when the configured BabyAI native runtime cannot be used safely."""


REQUIRED_BABYAI_SYMBOLS: tuple[str, ...] = (
    "babyai_native_abi_version",
    "babyai_native_runtime_create",
    "babyai_native_runtime_destroy",
    "babyai_native_model_open",
    "babyai_native_model_close",
    "babyai_native_last_error",
)


def _configure_abi(library: Any) -> None:
    library.babyai_native_abi_version.argtypes = []
    library.babyai_native_abi_version.restype = ctypes.c_uint32

    library.babyai_native_runtime_create.argtypes = [ctypes.POINTER(ctypes.c_void_p)]
    library.babyai_native_runtime_create.restype = ctypes.c_int32
    library.babyai_native_runtime_destroy.argtypes = [ctypes.c_void_p]
    library.babyai_native_runtime_destroy.restype = None

    library.babyai_native_model_open.argtypes = [
        ctypes.c_void_p,
        ctypes.c_char_p,
        ctypes.c_int32,
        ctypes.POINTER(ctypes.c_void_p),
    ]
    library.babyai_native_model_open.restype = ctypes.c_int32
    library.babyai_native_model_close.argtypes = [ctypes.c_void_p]
    library.babyai_native_model_close.restype = None

    library.babyai_native_last_error.argtypes = [ctypes.c_void_p]
    library.babyai_native_last_error.restype = ctypes.c_char_p


def _last_error(library: Any, runtime_pointer: ctypes.c_void_p) -> str:
    if not runtime_pointer.value:
        return ""
    raw = library.babyai_native_last_error(runtime_pointer)
    if not raw:
        return ""
    if isinstance(raw, bytes):
        return raw.decode("utf-8", errors="replace").strip()
    return str(raw).strip()


@dataclass(slots=True)
class NativeModelHandle:
    """Managed opaque GGUF model handle owned by one native runtime session."""

    runtime: NativeRuntimeSession
    path: Path
    pointer: ctypes.c_void_p
    _closed: bool = False

    @property
    def closed(self) -> bool:
        return self._closed

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        try:
            if self.pointer.value:
                self.runtime.handle.library.babyai_native_model_close(self.pointer)
        finally:
            self.pointer = ctypes.c_void_p()
            self.runtime._forget_model(self)

    def __enter__(self) -> NativeModelHandle:
        if self._closed:
            raise NativeRuntimeError("Native model handle is already closed.")
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()


@dataclass(slots=True)
class NativeRuntimeSession:
    """Managed backend session that always closes child models before shutdown."""

    handle: NativeRuntimeHandle
    pointer: ctypes.c_void_p
    _models: list[NativeModelHandle] = field(default_factory=list)
    _closed: bool = False

    @property
    def closed(self) -> bool:
        return self._closed

    def open_model(self, model_path: Path, *, n_gpu_layers: int = 0) -> NativeModelHandle:
        if self._closed or not self.pointer.value:
            raise NativeRuntimeError("Native runtime session is closed.")

        path = model_path.expanduser().resolve()
        out_model = ctypes.c_void_p()
        result = int(
            self.handle.library.babyai_native_model_open(
                self.pointer,
                str(path).encode("utf-8"),
                int(n_gpu_layers),
                ctypes.byref(out_model),
            )
        )
        if result != BABYAI_NATIVE_OK or not out_model.value:
            detail = _last_error(self.handle.library, self.pointer)
            suffix = f": {detail}" if detail else ""
            raise NativeRuntimeError(
                f"Could not open native GGUF model '{path}' (code {result}){suffix}"
            )

        model = NativeModelHandle(runtime=self, path=path, pointer=out_model)
        self._models.append(model)
        return model

    def _forget_model(self, model: NativeModelHandle) -> None:
        try:
            self._models.remove(model)
        except ValueError:
            pass

    def close(self) -> None:
        if self._closed:
            return

        # A llama model must be released before the backend session that owns it.
        for model in list(reversed(self._models)):
            model.close()

        self._closed = True
        if self.pointer.value:
            self.handle.library.babyai_native_runtime_destroy(self.pointer)
        self.pointer = ctypes.c_void_p()

    def __enter__(self) -> NativeRuntimeSession:
        if self._closed:
            raise NativeRuntimeError("Native runtime session is already closed.")
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()


@dataclass(slots=True)
class NativeRuntimeHandle:
    path: Path
    library: Any
    abi_version: int

    def open_runtime(self) -> NativeRuntimeSession:
        out_runtime = ctypes.c_void_p()
        result = int(self.library.babyai_native_runtime_create(ctypes.byref(out_runtime)))
        if result != BABYAI_NATIVE_OK or not out_runtime.value:
            raise NativeRuntimeError(f"Could not create BabyAI native runtime (code {result}).")
        return NativeRuntimeSession(handle=self, pointer=out_runtime)


@dataclass(slots=True)
class NativeRuntimeLoader:
    """Explicitly load BabyAI's stable native shim and validate ABI v1.

    llama.cpp is linked behind this DLL boundary. Readiness/status polling remains
    file-only; dynamic loading and backend initialization happen only through an
    explicit native lifecycle call.
    """

    path: Path

    def load(self) -> NativeRuntimeHandle:
        path = self.path.expanduser().resolve()
        if not path.is_file():
            raise NativeRuntimeError(f"Native runtime library not found: {path}")

        dll_directory = (
            os.add_dll_directory(str(path.parent))
            if os.name == "nt" and hasattr(os, "add_dll_directory")
            else nullcontext()
        )
        try:
            with dll_directory:
                library = ctypes.CDLL(str(path))
        except OSError as exc:
            raise NativeRuntimeError(f"Could not load native runtime library '{path}': {exc}") from exc

        missing = [name for name in REQUIRED_BABYAI_SYMBOLS if not hasattr(library, name)]
        if missing:
            names = ", ".join(missing)
            raise NativeRuntimeError(
                f"Native runtime library '{path}' does not satisfy BabyAI native ABI v1; "
                f"missing symbols: {names}"
            )

        _configure_abi(library)
        abi_version = int(library.babyai_native_abi_version())
        if abi_version != BABYAI_NATIVE_ABI_VERSION:
            raise NativeRuntimeError(
                f"Native runtime ABI mismatch: expected {BABYAI_NATIVE_ABI_VERSION}, got {abi_version}."
            )

        return NativeRuntimeHandle(path=path, library=library, abi_version=abi_version)

    def open_runtime(self) -> NativeRuntimeSession:
        return self.load().open_runtime()
