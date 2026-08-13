from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from .llm import LLMError, LLMProvider


@dataclass(slots=True)
class NativeBrainProvider(LLMProvider):
    """Reserved provider boundary for the future embedded GGUF runtime.

    This provider intentionally does not shell out to llama-cli/llama-server. The
    runtime and model paths are explicit so the next inference implementation can
    load the shared library in-process through NativeRuntimeLoader.
    """

    model_path: Path
    runtime_path: Path

    def generate(self, prompt: str) -> str:
        raise LLMError(
            "Native brain inference is not implemented in this build yet. "
            f"Configured GGUF model: {self.model_path}; runtime: {self.runtime_path}"
        )
