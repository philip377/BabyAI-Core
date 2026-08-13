from __future__ import annotations

import ctypes
import os
from contextlib import nullcontext
from dataclasses import dataclass
from pathlib import Path
from typing import Any


class NativeRuntimeError(RuntimeError):
    """Raised when the configured embedded llama.cpp runtime cannot be loaded."""


# Minimum lifecycle ABI BabyAI will need before model inference is implemented.
# These names are part of llama.cpp's public C API in include/llama.h.
REQUIRED_LLAMA_SYMBOLS: tuple[str, ...] = (
    "llama_backend_init",
    "llama_backend_free",
    "llama_model_default_params",
    "llama_context_default_params",
    "llama_model_load_from_file",
    "llama_model_free",
    "llama_init_from_model",
    "llama_free",
)


@dataclass(slots=True)
class NativeRuntimeHandle:
    path: Path
    library: Any


@dataclass(slots=True)
class NativeRuntimeLoader:
    """Explicitly load a local llama.cpp shared library and validate ABI v1.

    Merely constructing this object is side-effect free. `load()` performs the
    dynamic-library load, so readiness/status probes can remain file-only until
    BabyAI intentionally enters the native inference path.
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

        missing = [name for name in REQUIRED_LLAMA_SYMBOLS if not hasattr(library, name)]
        if missing:
            names = ", ".join(missing)
            raise NativeRuntimeError(
                f"Native runtime library '{path}' does not satisfy BabyAI llama.cpp ABI v1; "
                f"missing symbols: {names}"
            )

        return NativeRuntimeHandle(path=path, library=library)
