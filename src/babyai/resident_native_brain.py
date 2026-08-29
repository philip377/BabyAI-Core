from __future__ import annotations

import time
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path

from .llm import LLMError, LLMProvider
from .native_brain import (
    _NATIVE_STOP_SEQUENCES,
    _normalise_native_reply,
    _prepare_native_prompt,
    _requests_translation,
)
from .native_generation import NativeGenerationStop, generate_greedy
from .native_runtime import NativeModelHandle, NativeRuntimeError, NativeRuntimeLoader, NativeRuntimeSession
from .runtime_trace import trace


@dataclass(frozen=True, slots=True)
class ResidentNativeStreamResult:
    text: str
    generated_tokens: int
    output_bytes: int
    stop_reason: NativeGenerationStop
    first_token_ms: int | None = None
    generation_ms: int = 0


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
        return self.generate_stream(prompt, None).text

    def generate_stream(
        self,
        prompt: str,
        on_candidate: Callable[[str], None] | None,
    ) -> ResidentNativeStreamResult:
        """Generate one canonical reply while exposing untrusted raw text candidates.

        Candidate chunks have only passed native UTF-8 and stop-delimiter handling.
        They can still contain reasoning, response wrappers, or tool-call JSON and
        therefore must not be displayed before a higher layer validates them.
        """

        native_prompt = _prepare_native_prompt(prompt)
        started = time.monotonic()
        trace(
            "native.generate.start",
            prompt_chars=len(native_prompt),
            model_resident=self.model_is_resident,
            max_tokens=self.max_tokens,
            n_ctx=self.n_ctx,
            n_batch=self.n_batch,
            n_gpu_layers=self.n_gpu_layers,
        )
        try:
            model = self._ensure_model()
            trace(
                "native.generate.model_ready",
                elapsed_ms=round((time.monotonic() - started) * 1000),
            )
            generation_started = time.monotonic()
            result = generate_greedy(
                model,
                native_prompt,
                max_tokens=self.max_tokens,
                max_output_bytes=self.max_output_bytes,
                n_ctx=self.n_ctx,
                n_batch=self.n_batch,
                n_threads=self.n_threads,
                on_candidate=on_candidate,
                stop_sequences=_NATIVE_STOP_SEQUENCES,
                fit_context_to_prompt=True,
            )
            trace(
                "native.generate.done",
                elapsed_ms=round((time.monotonic() - generation_started) * 1000),
                total_elapsed_ms=round((time.monotonic() - started) * 1000),
                generated_tokens=result.generated_tokens,
                stop_reason=result.stop_reason,
                output_bytes=result.output_bytes,
            )
        except NativeRuntimeError as exc:
            trace(
                "native.generate.error",
                elapsed_ms=round((time.monotonic() - started) * 1000),
                error=type(exc).__name__,
            )
            self.close()
            raise LLMError(f"Native brain inference failed: {exc}") from exc

        text = _normalise_native_reply(
            result.text,
            allow_translation=_requests_translation(prompt),
        )
        if not text:
            raise LLMError(
                "Native brain returned no text "
                f"(stop reason: {result.stop_reason}, generated tokens: {result.generated_tokens})."
            )
        return ResidentNativeStreamResult(
            text=text,
            generated_tokens=result.generated_tokens,
            output_bytes=result.output_bytes,
            stop_reason=result.stop_reason,
            first_token_ms=result.first_token_ms,
            generation_ms=result.generation_ms,
        )

    def _ensure_model(self) -> NativeModelHandle:
        if self.model_is_resident:
            assert self._model is not None
            trace("native.model.reuse")
            return self._model

        runtime_started = time.monotonic()
        trace("native.runtime.open.start", runtime=self.runtime_path.name)
        runtime = NativeRuntimeLoader(self.runtime_path).open_runtime()
        trace(
            "native.runtime.open.done",
            elapsed_ms=round((time.monotonic() - runtime_started) * 1000),
        )
        try:
            model_started = time.monotonic()
            trace(
                "native.model.open.start",
                model=self.model_path.name,
                n_gpu_layers=self.n_gpu_layers,
            )
            model = runtime.open_model(self.model_path, n_gpu_layers=self.n_gpu_layers)
            trace(
                "native.model.open.done",
                elapsed_ms=round((time.monotonic() - model_started) * 1000),
            )
        except Exception:
            trace("native.model.open.error")
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
