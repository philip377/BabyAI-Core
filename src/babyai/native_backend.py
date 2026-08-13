from __future__ import annotations

import ctypes
import os
from contextlib import nullcontext
from dataclasses import dataclass
from pathlib import Path

from .native_runtime import BABYAI_NATIVE_ABI_VERSION, NativeRuntimeError


KNOWN_NATIVE_BACKENDS = frozenset({"cpu", "vulkan"})


@dataclass(frozen=True, slots=True)
class NativeBackendInfo:
    abi_version: int
    build_backend: str
    metadata_available: bool


@dataclass(frozen=True, slots=True)
class NativeAccelerationInfo:
    backend: NativeBackendInfo
    gpu_probe_available: bool
    gpu_available: bool


def _load_validated_library(path: Path) -> tuple[Path, ctypes.CDLL, int]:
    resolved = path.expanduser().resolve()
    if not resolved.is_file():
        raise NativeRuntimeError(f"Native runtime library not found: {resolved}")

    dll_directory = (
        os.add_dll_directory(str(resolved.parent))
        if os.name == "nt" and hasattr(os, "add_dll_directory")
        else nullcontext()
    )
    try:
        with dll_directory:
            library = ctypes.CDLL(str(resolved))
    except OSError as exc:
        raise NativeRuntimeError(f"Could not load native runtime library '{resolved}': {exc}") from exc

    if not hasattr(library, "babyai_native_abi_version"):
        raise NativeRuntimeError(
            f"Native runtime library '{resolved}' does not expose babyai_native_abi_version."
        )

    library.babyai_native_abi_version.argtypes = []
    library.babyai_native_abi_version.restype = ctypes.c_uint32
    abi_version = int(library.babyai_native_abi_version())
    if abi_version != BABYAI_NATIVE_ABI_VERSION:
        raise NativeRuntimeError(
            f"Native runtime ABI mismatch: expected {BABYAI_NATIVE_ABI_VERSION}, got {abi_version}."
        )

    return resolved, library, abi_version


def inspect_native_backend(path: Path) -> NativeBackendInfo:
    """Read optional build metadata without changing the stable ABI v6 contract.

    Older ABI v6 DLLs do not expose ``babyai_native_build_backend`` and remain
    compatible; they are reported as ``unknown``. New DLLs must report one of
    BabyAI's known build profiles so automatic backend selection can fail closed
    instead of guessing from filenames or hardware.
    """

    _, library, abi_version = _load_validated_library(path)

    if not hasattr(library, "babyai_native_build_backend"):
        return NativeBackendInfo(
            abi_version=abi_version,
            build_backend="unknown",
            metadata_available=False,
        )

    library.babyai_native_build_backend.argtypes = []
    library.babyai_native_build_backend.restype = ctypes.c_char_p
    raw = library.babyai_native_build_backend()
    if not raw:
        raise NativeRuntimeError("Native runtime returned empty build-backend metadata.")

    try:
        backend = raw.decode("utf-8").strip().lower() if isinstance(raw, bytes) else str(raw).strip().lower()
    except UnicodeDecodeError as exc:
        raise NativeRuntimeError("Native runtime build-backend metadata is not valid UTF-8.") from exc

    if backend not in KNOWN_NATIVE_BACKENDS:
        raise NativeRuntimeError(f"Native runtime reported unsupported build backend: {backend!r}.")

    return NativeBackendInfo(
        abi_version=abi_version,
        build_backend=backend,
        metadata_available=True,
    )


def inspect_native_acceleration(path: Path) -> NativeAccelerationInfo:
    """Probe actual GPU offload availability without loading a GGUF model.

    The GPU probe is an additive ABI v6 extension. Older v6 runtimes remain valid
    but conservatively report no probe and no GPU availability. New runtimes create
    a lightweight BabyAI runtime so llama.cpp can register its compiled backends,
    then ask whether a real GPU/iGPU offload device is present.
    """

    backend = inspect_native_backend(path)
    _, library, _ = _load_validated_library(path)
    if not hasattr(library, "babyai_native_runtime_gpu_available"):
        return NativeAccelerationInfo(
            backend=backend,
            gpu_probe_available=False,
            gpu_available=False,
        )

    for symbol in ("babyai_native_runtime_create", "babyai_native_runtime_destroy"):
        if not hasattr(library, symbol):
            raise NativeRuntimeError(f"Native runtime does not expose required symbol: {symbol}.")

    library.babyai_native_runtime_create.argtypes = [ctypes.POINTER(ctypes.c_void_p)]
    library.babyai_native_runtime_create.restype = ctypes.c_int32
    library.babyai_native_runtime_destroy.argtypes = [ctypes.c_void_p]
    library.babyai_native_runtime_destroy.restype = None
    library.babyai_native_runtime_gpu_available.argtypes = [ctypes.c_void_p]
    library.babyai_native_runtime_gpu_available.restype = ctypes.c_int32

    runtime = ctypes.c_void_p()
    result = int(library.babyai_native_runtime_create(ctypes.byref(runtime)))
    if result != 0 or not runtime.value:
        raise NativeRuntimeError(
            f"Native runtime could not initialize for GPU capability probe (result={result})."
        )

    try:
        available = bool(library.babyai_native_runtime_gpu_available(runtime))
    finally:
        library.babyai_native_runtime_destroy(runtime)

    return NativeAccelerationInfo(
        backend=backend,
        gpu_probe_available=True,
        gpu_available=available,
    )
