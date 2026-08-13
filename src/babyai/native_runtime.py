from __future__ import annotations

import ctypes
import os
from contextlib import nullcontext
from dataclasses import dataclass
from pathlib import Path
from typing import Any


BABYAI_NATIVE_ABI_VERSION = 1


class NativeRuntimeError(RuntimeError):
    """Raised when the configured BabyAI native runtime cannot be loaded."""


REQUIRED_BABYAI_SYMBOLS: tuple[str, ...] = (
    "babyai_native_abi_version",
    "babyai_native_runtime_create",
    "babyai_native_runtime_destroy",
    "babyai_native_model_open",
    "babyai_native_model_close",
    "babyai_native_last_error",
)


@dataclass(slots=True)
class NativeRuntimeHandle:
    path: Path
    library: Any
    abi_version: int


@dataclass(slots=True)
class NativeRuntimeLoader:
    """Explicitly load BabyAI's stable native shim and validate ABI v1.

    llama.cpp is linked behind this DLL boundary. Readiness/status polling remains
    file-only; dynamic loading happens only when `load()` is explicitly called.
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

        abi_fn = library.babyai_native_abi_version
        abi_fn.restype = ctypes.c_uint32
        abi_version = int(abi_fn())
        if abi_version != BABYAI_NATIVE_ABI_VERSION:
            raise NativeRuntimeError(
                f"Native runtime ABI mismatch: expected {BABYAI_NATIVE_ABI_VERSION}, got {abi_version}."
            )

        return NativeRuntimeHandle(path=path, library=library, abi_version=abi_version)
