from __future__ import annotations

import pytest

from babyai.brain import build_brain_provider
from babyai.config import BabyAIConfig
from babyai.llm import LLMError
from babyai.native_acceleration import NativeRuntimeSelection
from babyai.native_brain import NativeBrainProvider
from babyai.native_runtime import NativeRuntimeError


def test_default_native_acceleration_remains_cpu(tmp_path):
    config = BabyAIConfig(data_dir=tmp_path, provider="native")
    provider = build_brain_provider(config)

    assert isinstance(provider, NativeBrainProvider)
    assert provider.runtime_path == config.native_runtime_file.resolve()
    assert provider.n_gpu_layers == 0


def test_auto_selection_flows_runtime_and_full_offload_to_provider(tmp_path, monkeypatch):
    selected_runtime = tmp_path / "runtime" / "vulkan" / "babyai_native.dll"
    monkeypatch.setattr(
        "babyai.brain.select_native_runtime",
        lambda *args: NativeRuntimeSelection("vulkan", selected_runtime, -1),
    )
    config = BabyAIConfig(data_dir=tmp_path, provider="native", native_acceleration="auto")

    provider = build_brain_provider(config)

    assert isinstance(provider, NativeBrainProvider)
    assert provider.runtime_path == selected_runtime
    assert provider.n_gpu_layers == -1


def test_selection_error_preserves_llm_error_boundary(tmp_path, monkeypatch):
    def fail(*args):
        raise NativeRuntimeError("no compatible runtime")

    monkeypatch.setattr("babyai.brain.select_native_runtime", fail)
    config = BabyAIConfig(data_dir=tmp_path, provider="native", native_acceleration="vulkan")

    with pytest.raises(LLMError, match="Native brain inference failed: no compatible runtime"):
        build_brain_provider(config)


def test_config_default_vulkan_runtime_is_isolated_from_cpu_runtime(tmp_path):
    config = BabyAIConfig(data_dir=tmp_path, provider="native")

    assert config.native_acceleration == "cpu"
    assert config.native_vulkan_runtime_file == config.native_runtime_file.parent / "vulkan" / config.native_runtime_file.name
