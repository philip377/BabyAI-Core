from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from .native_backend import inspect_native_acceleration
from .native_runtime import NativeRuntimeError


MODES = frozenset({"cpu", "vulkan", "hybrid", "auto"})
HYBRID_GPU_LAYERS = 20


@dataclass(frozen=True, slots=True)
class NativeRuntimeSelection:
    mode: str
    runtime_path: Path
    n_gpu_layers: int


def select_native_runtime(mode: str, cpu_path: Path, vulkan_path: Path) -> NativeRuntimeSelection:
    mode = mode.strip().lower()
    if mode not in MODES:
        raise NativeRuntimeError(f"Unsupported native acceleration mode: {mode}")

    cpu_path = cpu_path.expanduser().resolve()
    vulkan_path = vulkan_path.expanduser().resolve()
    if mode == "cpu":
        return NativeRuntimeSelection("cpu", cpu_path, 0)
    if mode == "vulkan":
        return _vulkan(vulkan_path)
    if mode == "hybrid":
        return _vulkan(vulkan_path, n_gpu_layers=HYBRID_GPU_LAYERS, mode="hybrid")

    if vulkan_path.is_file():
        try:
            return _vulkan(vulkan_path)
        except NativeRuntimeError:
            pass
    return NativeRuntimeSelection("cpu", cpu_path, 0)


def _vulkan(
    path: Path,
    *,
    n_gpu_layers: int = -1,
    mode: str = "vulkan",
) -> NativeRuntimeSelection:
    if not path.is_file():
        raise NativeRuntimeError(f"Vulkan native runtime not found: {path}")
    info = inspect_native_acceleration(path)
    if info.backend.build_backend != "vulkan":
        raise NativeRuntimeError("Configured Vulkan runtime is not a Vulkan build.")
    if not info.gpu_probe_available or not info.gpu_available:
        raise NativeRuntimeError("No usable Vulkan acceleration device detected.")
    return NativeRuntimeSelection(mode, path, n_gpu_layers)
