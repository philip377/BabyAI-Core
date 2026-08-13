from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from .llm import LLMError, LLMProvider
from .native_generation import generate_greedy
from .native_runtime import NativeRuntimeError, NativeRuntimeLoader


@dataclass(slots=True)
class NativeBrainProvider(LLMProvider):
    """Run bounded GGUF inference in-process through BabyAI's stable native shim.

    Each generate call owns a fresh runtime/model/context lifetime. This first
    provider integration favors explicit cleanup and a narrow failure boundary over
    keeping native model state resident between commands. No subprocesses or
    automatic downloads are used.
    """

    model_path: Path
    runtime_path: Path
    max_tokens: int = 256
    max_output_bytes: int = 1_048_576
    n_ctx: int = 4096
    n_batch: int = 4096
    n_threads: int = 0
    n_gpu_layers: int = 0

    def generate(self, prompt: str) -> str:
        try:
            with NativeRuntimeLoader(self.runtime_path).open_runtime() as runtime:
                with runtime.open_model(self.model_path, n_gpu_layers=self.n_gpu_layers) as model:
                    result = generate_greedy(
                        model,
                        prompt,
                        max_tokens=self.max_tokens,
                        max_output_bytes=self.max_output_bytes,
                        n_ctx=self.n_ctx,
                        n_batch=self.n_batch,
                        n_threads=self.n_threads,
                    )
        except NativeRuntimeError as exc:
            raise LLMError(f"Native brain inference failed: {exc}") from exc

        text = result.text.strip()
        if not text:
            raise LLMError(
                "Native brain returned no text "
                f"(stop reason: {result.stop_reason}, generated tokens: {result.generated_tokens})."
            )
        return text
