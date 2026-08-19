from __future__ import annotations

from types import SimpleNamespace

import pytest

from babyai.native_acceleration import HYBRID_GPU_LAYERS, select_native_runtime
from babyai.native_runtime import NativeRuntimeError


def test_cpu_mode_keeps_cpu_runtime(tmp_path):
    cpu = tmp_path / "cpu.dll"
    vulkan = tmp_path / "vulkan.dll"

    selected = select_native_runtime("cpu", cpu, vulkan)

    assert selected.mode == "cpu"
    assert selected.runtime_path == cpu.resolve()
    assert selected.n_gpu_layers == 0


def test_auto_uses_cpu_when_vulkan_runtime_is_absent(tmp_path):
    selected = select_native_runtime("auto", tmp_path / "cpu.dll", tmp_path / "missing.dll")
    assert selected.mode == "cpu"
    assert selected.n_gpu_layers == 0


def test_vulkan_requires_runtime_file(tmp_path):
    with pytest.raises(NativeRuntimeError, match="Vulkan native runtime not found"):
        select_native_runtime("vulkan", tmp_path / "cpu.dll", tmp_path / "missing.dll")


def test_vulkan_uses_full_offload_when_probe_is_positive(tmp_path, monkeypatch):
    vulkan = tmp_path / "vulkan.dll"
    vulkan.write_bytes(b"x")
    monkeypatch.setattr(
        "babyai.native_acceleration.inspect_native_acceleration",
        lambda path: SimpleNamespace(
            backend=SimpleNamespace(build_backend="vulkan"),
            gpu_probe_available=True,
            gpu_available=True,
        ),
    )

    selected = select_native_runtime("vulkan", tmp_path / "cpu.dll", vulkan)

    assert selected.mode == "vulkan"
    assert selected.runtime_path == vulkan.resolve()
    assert selected.n_gpu_layers == -1


def test_hybrid_uses_partial_vulkan_offload_when_probe_is_positive(tmp_path, monkeypatch):
    vulkan = tmp_path / "vulkan.dll"
    vulkan.write_bytes(b"x")
    monkeypatch.setattr(
        "babyai.native_acceleration.inspect_native_acceleration",
        lambda *args: SimpleNamespace(
            backend=SimpleNamespace(build_backend="vulkan"),
            gpu_probe_available=True,
            gpu_available=True,
        ),
    )

    selected = select_native_runtime("hybrid", tmp_path / "cpu.dll", vulkan)

    assert selected.mode == "hybrid"
    assert selected.runtime_path == vulkan.resolve()
    assert selected.n_gpu_layers == HYBRID_GPU_LAYERS == 20


def test_auto_falls_back_to_cpu_when_probe_is_negative(tmp_path, monkeypatch):
    vulkan = tmp_path / "vulkan.dll"
    vulkan.write_bytes(b"x")
    monkeypatch.setattr(
        "babyai.native_acceleration.inspect_native_acceleration",
        lambda path: SimpleNamespace(
            backend=SimpleNamespace(build_backend="vulkan"),
            gpu_probe_available=True,
            gpu_available=False,
        ),
    )

    selected = select_native_runtime("auto", tmp_path / "cpu.dll", vulkan)

    assert selected.mode == "cpu"
    assert selected.n_gpu_layers == 0


def test_invalid_mode_is_rejected(tmp_path):
    with pytest.raises(NativeRuntimeError, match="Unsupported native acceleration mode"):
        select_native_runtime("other", tmp_path / "cpu.dll", tmp_path / "vulkan.dll")
