from __future__ import annotations

import json
import urllib.error
import urllib.request
from collections.abc import Callable
from dataclasses import asdict, dataclass

from .config import BabyAIConfig
from .llm import EchoProvider, LLMError, LLMProvider, OllamaProvider
from .native_acceleration import select_native_runtime
from .native_brain import NativeBrainProvider
from .native_runtime import NativeRuntimeError


class BrainProviderError(ValueError):
    """Raised when BabyAI cannot construct the configured brain provider."""


@dataclass(frozen=True, slots=True)
class BrainRuntimeStatus:
    provider: str
    model: str
    state: str
    ready: bool
    detail: str

    def as_dict(self) -> dict[str, object]:
        return asdict(self)


def _build_echo(config: BabyAIConfig) -> LLMProvider:
    return EchoProvider()


def _build_ollama(config: BabyAIConfig) -> LLMProvider:
    return OllamaProvider(model=config.model, base_url=config.ollama_url)


def _build_native(config: BabyAIConfig) -> LLMProvider:
    try:
        selection = select_native_runtime(
            config.native_acceleration,
            config.native_runtime_file,
            config.native_vulkan_runtime_file,
        )
    except NativeRuntimeError as exc:
        raise LLMError(f"Native brain inference failed: {exc}") from exc

    return NativeBrainProvider(
        model_path=config.native_model_file,
        runtime_path=selection.runtime_path,
        n_gpu_layers=selection.n_gpu_layers,
    )


_PROVIDER_BUILDERS: dict[str, Callable[[BabyAIConfig], LLMProvider]] = {
    "echo": _build_echo,
    "ollama": _build_ollama,
    "native": _build_native,
}


def supported_brain_providers() -> tuple[str, ...]:
    """Return provider identifiers supported by this BabyAI build."""

    return tuple(_PROVIDER_BUILDERS)


def build_brain_provider(config: BabyAIConfig) -> LLMProvider:
    """Construct the configured brain behind one stable Core boundary."""

    try:
        builder = _PROVIDER_BUILDERS[config.provider]
    except KeyError as exc:
        supported = ", ".join(supported_brain_providers())
        raise BrainProviderError(
            f"Unknown BABYAI_PROVIDER={config.provider!r}. Supported providers: {supported}."
        ) from exc
    return builder(config)


def probe_brain_runtime(config: BabyAIConfig) -> BrainRuntimeStatus:
    """Read-only readiness probe shared by desktop and future launch surfaces."""

    if config.provider == "echo":
        return BrainRuntimeStatus(
            provider="echo",
            model=config.model,
            state="ready",
            ready=True,
            detail="Echo diagnostics provider is ready.",
        )

    if config.provider == "native":
        model_path = config.native_model_file
        if not model_path.is_file():
            return BrainRuntimeStatus(
                provider="native",
                model=config.model,
                state="native_model_missing",
                ready=False,
                detail=f"Native GGUF model not found at: {model_path}",
            )

        runtime_path = (
            config.native_vulkan_runtime_file
            if config.native_acceleration == "vulkan"
            else config.native_runtime_file
        )
        if not runtime_path.is_file():
            return BrainRuntimeStatus(
                provider="native",
                model=config.model,
                state="native_runtime_missing",
                ready=False,
                detail=f"BabyAI native runtime library not found at: {runtime_path}",
            )

        return BrainRuntimeStatus(
            provider="native",
            model=config.model,
            state="ready",
            ready=True,
            detail=(
                "Native GGUF model and BabyAI runtime library are configured. "
                "The runtime ABI and model are validated when generation is explicitly requested."
            ),
        )

    if config.provider != "ollama":
        return BrainRuntimeStatus(
            provider=config.provider,
            model=config.model,
            state="unsupported_provider",
            ready=False,
            detail=f"Unsupported provider: {config.provider}",
        )

    request = urllib.request.Request(
        f"{config.ollama_url.rstrip('/')}/api/tags",
        headers={"Accept": "application/json"},
        method="GET",
    )
    try:
        with urllib.request.urlopen(request, timeout=1.5) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except (urllib.error.URLError, TimeoutError, OSError):
        return BrainRuntimeStatus(
            provider="ollama",
            model=config.model,
            state="unavailable",
            ready=False,
            detail="Ollama is not reachable.",
        )
    except (json.JSONDecodeError, UnicodeDecodeError, ValueError, TypeError):
        return BrainRuntimeStatus(
            provider="ollama",
            model=config.model,
            state="unavailable",
            ready=False,
            detail="Ollama returned an invalid readiness response.",
        )

    models = payload.get("models") if isinstance(payload, dict) else None
    installed: set[str] = set()
    if isinstance(models, list):
        for model in models:
            if not isinstance(model, dict):
                continue
            for key in ("name", "model"):
                value = model.get(key)
                if isinstance(value, str) and value.strip():
                    installed.add(value.strip())

    if config.model not in installed:
        return BrainRuntimeStatus(
            provider="ollama",
            model=config.model,
            state="model_missing",
            ready=False,
            detail=f"Ollama is online, but model '{config.model}' is not installed.",
        )

    return BrainRuntimeStatus(
        provider="ollama",
        model=config.model,
        state="ready",
        ready=True,
        detail="Ollama and the configured model are ready.",
    )
