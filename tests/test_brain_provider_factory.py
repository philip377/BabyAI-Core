from __future__ import annotations

import pytest

from babyai.brain import BrainProviderError, build_brain_provider, supported_brain_providers
from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommandError, DesktopCommands
from babyai.llm import EchoProvider, OllamaProvider
from babyai.native_brain import NativeBrainProvider


def test_factory_builds_echo_provider(tmp_path):
    config = BabyAIConfig(data_dir=tmp_path, provider="echo")

    provider = build_brain_provider(config)

    assert isinstance(provider, EchoProvider)


def test_factory_builds_ollama_provider_from_config(tmp_path):
    config = BabyAIConfig(
        data_dir=tmp_path,
        provider="ollama",
        model="example-model",
        ollama_url="http://127.0.0.1:9999",
    )

    provider = build_brain_provider(config)

    assert isinstance(provider, OllamaProvider)
    assert provider.model == "example-model"
    assert provider.base_url == "http://127.0.0.1:9999"


def test_factory_builds_native_generation_provider(tmp_path):
    model_path = tmp_path / "models" / "babyai.gguf"
    runtime_path = tmp_path / "runtime" / "babyai_native.dll"
    config = BabyAIConfig(
        data_dir=tmp_path,
        provider="native",
        native_model_path=model_path,
        native_runtime_path=runtime_path,
    )

    provider = build_brain_provider(config)

    assert isinstance(provider, NativeBrainProvider)
    assert provider.model_path == model_path
    assert provider.runtime_path == runtime_path
    assert provider.max_tokens == 256
    assert provider.n_ctx == 4096
    assert provider.n_batch == 4096


def test_factory_exposes_supported_provider_names():
    assert supported_brain_providers() == ("echo", "ollama", "native")


def test_factory_rejects_unknown_provider_with_stable_error(tmp_path):
    config = BabyAIConfig(data_dir=tmp_path, provider="bogus")

    with pytest.raises(BrainProviderError, match="Unknown BABYAI_PROVIDER='bogus'"):
        build_brain_provider(config)


def test_desktop_commands_translate_factory_error(tmp_path):
    commands = DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider="bogus"))

    with pytest.raises(DesktopCommandError, match="Unknown BABYAI_PROVIDER='bogus'"):
        commands._provider()
