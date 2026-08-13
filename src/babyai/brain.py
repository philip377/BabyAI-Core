from __future__ import annotations

from collections.abc import Callable

from .config import BabyAIConfig
from .llm import EchoProvider, LLMProvider, OllamaProvider


class BrainProviderError(ValueError):
    """Raised when BabyAI cannot construct the configured brain provider."""


def _build_echo(config: BabyAIConfig) -> LLMProvider:
    return EchoProvider()


def _build_ollama(config: BabyAIConfig) -> LLMProvider:
    return OllamaProvider(model=config.model, base_url=config.ollama_url)


_PROVIDER_BUILDERS: dict[str, Callable[[BabyAIConfig], LLMProvider]] = {
    "echo": _build_echo,
    "ollama": _build_ollama,
}


def supported_brain_providers() -> tuple[str, ...]:
    """Return provider identifiers supported by this BabyAI build."""

    return tuple(_PROVIDER_BUILDERS)


def build_brain_provider(config: BabyAIConfig) -> LLMProvider:
    """Construct the configured brain behind one stable Core boundary.

    Keep provider-specific construction here so future embedded/native runtimes can
    be added without teaching CLI, desktop commands, or cognition components how
    each backend is created.
    """

    try:
        builder = _PROVIDER_BUILDERS[config.provider]
    except KeyError as exc:
        supported = ", ".join(supported_brain_providers())
        raise BrainProviderError(
            f"Unknown BABYAI_PROVIDER={config.provider!r}. Supported providers: {supported}."
        ) from exc
    return builder(config)
