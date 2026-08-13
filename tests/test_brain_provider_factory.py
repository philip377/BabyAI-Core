from __future__ import annotations

import pytest

from babyai.brain import BrainProviderError, build_brain_provider, supported_brain_providers
from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommandError, DesktopCommands
from babyai.llm import EchoProvider, OllamaProvider


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


def test_factory_exposes_supported_provider_names():
    assert supported_brain_providers() == ("echo", "ollama")


def test_factory_rejects_unknown_provider_with_stable_error(tmp_path):
    config = BabyAIConfig(data_dir=tmp_path, provider="native")

    with pytest.raises(BrainProviderError, match="Unknown BABYAI_PROVIDER='native'"):
        build_brain_provider(config)


def test_desktop_commands_translate_factory_error(tmp_path):
    commands = DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider="native"))

    with pytest.raises(DesktopCommandError, match="Unknown BABYAI_PROVIDER='native'"):
        commands._provider()
