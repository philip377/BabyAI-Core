from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from .llm import LLMError, LLMProvider


@dataclass(slots=True)
class NativeBrainProvider(LLMProvider):
    """Reserved provider boundary for the future embedded GGUF runtime.

    This class deliberately does not shell out to llama-cli/llama-server and does
    not pretend native inference is available before BabyAI links an embedded
    runtime. Keeping the provider selectable now lets configuration, readiness,
    diagnostics, and desktop UX evolve before the native inference implementation.
    """

    model_path: Path

    def generate(self, prompt: str) -> str:
        raise LLMError(
            "Native brain runtime is not linked in this build yet. "
            f"Configured GGUF model: {self.model_path}"
        )
