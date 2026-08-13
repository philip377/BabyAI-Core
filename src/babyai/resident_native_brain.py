from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

from .llm import LLMError, LLMProvider
from .native_brain import _NATIVE_STOP_SEQUENCES, _normalise_native_reply, _prepare_native_prompt
from .native_generation import generate_greedy
from .native_runtime import NativeModelHandle, NativeRuntimeError, NativeRuntimeLoader, NativeRuntimeSession


@dataclass(slots=True)
class ResidentNativeBrainProvider(LLMProvider):
    """Keep one GGUF model loaded while each response uses a fresh context."""

    model_path: Path
    runtime_path: Path
    max_tokens: int = 128
    max_output_bytes: int = 1_048_576
    n_ctx: int = 4096
    n_batch: int = 4096
    n_threads: int = 0
    n_gpu_layers: int = 0
    _runtime: NativeRuntimeSession | None = field(default=None, init=False, repr=False)
    _model: NativeModelHandle | None = field(default=None, init=False, repr=False)

    @property
    def model_is_resident(self) -> bool:
        return self._model is not None and not self._model.closed

    def generate(self, prompt: str) -> str:
        native_prompt = _prepare_native_prompt(prompt)
        try:
            model = self._ensure_model()
            result = generate_greedy(
                model,
                native_prompt,
                max_tokens=self.max_tokens,
                max_output_bytes=self.max_output_bytes,
                n_ctx=self.n_ctx,
                n_batch=self.n_batch,
                n_threads=self.n_threads,
                stop_sequences=_NATIVE_STOP_SEQUENCES,
            )
        except NativeRuntimeError as exc:
            self.close()
            raise LLMError(f"Native brain inference failed: {exc}") from exc

        text = _normalise_native_reply(result.text)
        if not text:
            raise LLMError(
                "Native brain returned no text "
                f"(stop reason: {result.stop_reason}, generated tokens: {result.generated_tokens})."
            )
        return text

    def _ensure_model(self) -> NativeModelHandle:
        if self.model_is_resident:
            assert self._model is not None
            return self._model

        runtime = NativeRuntimeLoader(self.runtime_path).open_runtime()
        try:
            model = runtime.open_model(self.model_path, n_gpu_layers=self.n_gpu_layers)
        except Exception:
            runtime.close()
            raise

        self._runtime = runtime
        self._model = model
        return model

    def close(self) -> None:
        model = self._model
        runtime = self._runtime
        self._model = None
        self._runtime = None

        if model is not None:
            model.close()
        if runtime is not None:
            runtime.close()

    def __enter__(self) -> ResidentNativeBrainProvider:
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()
