from __future__ import annotations

import json
import urllib.error
import urllib.request
from abc import ABC, abstractmethod
from dataclasses import dataclass


class LLMError(RuntimeError):
    """Raised when a language-model provider cannot produce a response."""


class LLMProvider(ABC):
    @abstractmethod
    def generate(self, prompt: str) -> str:
        raise NotImplementedError


class EchoProvider(LLMProvider):
    """Deterministic provider used by tests and offline diagnostics."""

    def generate(self, prompt: str) -> str:
        return f"[echo] {prompt}"


@dataclass(slots=True)
class OllamaProvider(LLMProvider):
    """Talk to a local Ollama server without adding an SDK dependency."""

    model: str = "qwen3:8b"
    base_url: str = "http://127.0.0.1:11434"
    timeout: float = 120.0

    def generate(self, prompt: str) -> str:
        payload = json.dumps(
            {"model": self.model, "prompt": prompt, "stream": False}
        ).encode("utf-8")
        request = urllib.request.Request(
            f"{self.base_url.rstrip('/')}/api/generate",
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                data = json.loads(response.read().decode("utf-8"))
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            raise LLMError(
                "Cannot reach local Ollama. Start Ollama and make sure its API is available."
            ) from exc
        except json.JSONDecodeError as exc:
            raise LLMError("Ollama returned an invalid JSON response.") from exc

        text = data.get("response")
        if not isinstance(text, str) or not text.strip():
            error = data.get("error")
            raise LLMError(str(error or "Ollama returned an empty response."))
        return text.strip()
