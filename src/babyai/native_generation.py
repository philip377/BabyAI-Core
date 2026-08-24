from __future__ import annotations

import codecs
import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from typing import Literal

from .native_runtime import MAX_NATIVE_TEXT_BYTES, NativeModelHandle, NativeRuntimeError
from .runtime_trace import trace


MAX_NATIVE_GENERATION_TOKENS = 4096
_CONTEXT_FIT_GRANULARITY = 256
_CONTEXT_FIT_MINIMUM = 512

NativeGenerationStop = Literal[
    "eog",
    "max_tokens",
    "context_limit",
    "output_limit",
    "cancelled",
    "stop_sequence",
]


@dataclass(frozen=True, slots=True)
class NativeGenerationResult:
    text: str
    generated_tokens: int
    output_bytes: int
    stop_reason: NativeGenerationStop
    first_token_ms: int | None = None
    generation_ms: int = 0


def _decode_complete_utf8(raw: bytes, *, final: bool) -> str:
    decoder = codecs.getincrementaldecoder("utf-8")("strict")
    try:
        return decoder.decode(raw, final=final)
    except UnicodeDecodeError as exc:
        raise NativeRuntimeError("Native generation produced invalid UTF-8 output bytes.") from exc


def _encode_stop_sequences(stop_sequences: Sequence[str]) -> tuple[bytes, ...]:
    encoded: list[bytes] = []
    for stop in stop_sequences:
        if not isinstance(stop, str) or not stop:
            raise NativeRuntimeError("Native generation stop sequences must be non-empty strings.")
        raw = stop.encode("utf-8")
        if raw not in encoded:
            encoded.append(raw)
    encoded.sort(key=len, reverse=True)
    return tuple(encoded)


def _pending_stop_prefix_length(output: bytearray, stop_sequences: tuple[bytes, ...]) -> int:
    """Return bytes that could still grow into a managed stop delimiter."""

    pending = 0
    for stop in stop_sequences:
        maximum = min(len(output), len(stop) - 1)
        for length in range(maximum, 0, -1):
            if output.endswith(stop[:length]):
                pending = max(pending, length)
                break
    return pending


def _first_stop_offset(
    output: bytearray,
    stop_sequences: tuple[bytes, ...],
    *,
    search_start: int,
) -> int | None:
    """Find the earliest complete delimiter newly reachable in the output."""

    earliest: int | None = None
    for stop in stop_sequences:
        offset = output.find(stop, search_start)
        if offset >= 0 and (earliest is None or offset < earliest):
            earliest = offset
    return earliest


def _fit_context_limits(
    *,
    prompt_tokens: int,
    max_tokens: int,
    n_ctx: int,
    n_batch: int,
) -> tuple[int, int]:
    """Right-size per-response buffers without changing configured safety ceilings."""

    def rounded(value: int) -> int:
        return (
            (value + _CONTEXT_FIT_GRANULARITY - 1)
            // _CONTEXT_FIT_GRANULARITY
            * _CONTEXT_FIT_GRANULARITY
        )

    effective_ctx = n_ctx
    required_ctx = prompt_tokens + max_tokens
    if n_ctx > 0 and required_ctx <= n_ctx:
        effective_ctx = min(n_ctx, max(_CONTEXT_FIT_MINIMUM, rounded(required_ctx)))

    effective_batch = n_batch
    if n_batch > 0 and prompt_tokens <= n_batch:
        effective_batch = min(n_batch, max(_CONTEXT_FIT_GRANULARITY, rounded(prompt_tokens)))

    return effective_ctx, effective_batch


def generate_greedy(
    model: NativeModelHandle,
    prompt: str,
    *,
    max_tokens: int = 256,
    max_output_bytes: int = 1_048_576,
    n_ctx: int = 0,
    n_batch: int = 0,
    n_threads: int = 0,
    cancel_check: Callable[[], bool] | None = None,
    on_candidate: Callable[[str], None] | None = None,
    stop_sequences: Sequence[str] = (),
    fit_context_to_prompt: bool = False,
) -> NativeGenerationResult:
    """Generate bounded text through the native ABI v6 sample/decode state machine.

    Cancellation is cooperative at token boundaries. Token pieces are accumulated as
    raw bytes and decoded together so UTF-8 code points may safely span token pieces.
    If generation stops at a caller-imposed boundary while a final code point is only
    partially available, that incomplete suffix is omitted rather than replaced.

    Optional stop sequences are matched against the accumulated raw UTF-8 bytes after
    the sampled token is committed. A matching delimiter is removed from returned text.
    This lets managed chat policy stop a model before it starts inventing the next turn
    without extending BabyAI's native ABI.

    ``on_candidate`` receives incrementally decoded model text. It is an untrusted raw
    candidate, not display-safe assistant text: callers must still apply the provider's
    normalization and tool-call policy. Candidate delivery withholds both incomplete
    UTF-8 code points and any suffix that may still become a managed stop delimiter.
    """

    if not isinstance(prompt, str):
        raise NativeRuntimeError("Native generation prompt must be a string.")
    if isinstance(max_tokens, bool) or not isinstance(max_tokens, int) or max_tokens <= 0:
        raise NativeRuntimeError("Native generation max_tokens must be a positive integer.")
    if max_tokens > MAX_NATIVE_GENERATION_TOKENS:
        raise NativeRuntimeError(
            f"Native generation max_tokens exceeds the safety limit of {MAX_NATIVE_GENERATION_TOKENS}."
        )
    if (
        isinstance(max_output_bytes, bool)
        or not isinstance(max_output_bytes, int)
        or max_output_bytes <= 0
        or max_output_bytes > MAX_NATIVE_TEXT_BYTES
    ):
        raise NativeRuntimeError(
            f"Native generation max_output_bytes must be between 1 and {MAX_NATIVE_TEXT_BYTES}."
        )
    for name, value in (("n_ctx", n_ctx), ("n_batch", n_batch), ("n_threads", n_threads)):
        if isinstance(value, bool) or not isinstance(value, int) or value < 0:
            raise NativeRuntimeError(f"Native generation {name} must be a non-negative integer.")
    if cancel_check is not None and not callable(cancel_check):
        raise NativeRuntimeError("Native generation cancel_check must be callable.")
    if on_candidate is not None and not callable(on_candidate):
        raise NativeRuntimeError("Native generation on_candidate must be callable.")
    if isinstance(stop_sequences, (str, bytes)) or not isinstance(stop_sequences, Sequence):
        raise NativeRuntimeError("Native generation stop_sequences must be a sequence of strings.")
    if not isinstance(fit_context_to_prompt, bool):
        raise NativeRuntimeError("Native generation fit_context_to_prompt must be a boolean.")
    encoded_stops = _encode_stop_sequences(stop_sequences)
    longest_encoded_stop = max((len(stop) for stop in encoded_stops), default=0)

    tokenize_started = time.monotonic()
    trace("native.tokenize.start", prompt_chars=len(prompt))
    prompt_tokens = model.tokenize(prompt, add_special=True, parse_special=True)
    trace(
        "native.tokenize.done",
        prompt_tokens=len(prompt_tokens),
        elapsed_ms=round((time.monotonic() - tokenize_started) * 1000),
    )
    if not prompt_tokens:
        raise NativeRuntimeError("Native generation prompt tokenized to an empty sequence.")

    effective_n_ctx, effective_n_batch = n_ctx, n_batch
    if fit_context_to_prompt:
        effective_n_ctx, effective_n_batch = _fit_context_limits(
            prompt_tokens=len(prompt_tokens),
            max_tokens=max_tokens,
            n_ctx=n_ctx,
            n_batch=n_batch,
        )
        trace(
            "native.context.fit",
            configured_n_ctx=n_ctx,
            effective_n_ctx=effective_n_ctx,
            configured_n_batch=n_batch,
            effective_n_batch=effective_n_batch,
            reserved_output_tokens=max_tokens,
        )

    output = bytearray()
    generated = 0
    first_token_ms: int | None = None
    candidate_offset = 0
    candidate_decoder = (
        codecs.getincrementaldecoder("utf-8")("strict")
        if on_candidate is not None
        else None
    )

    context_started = time.monotonic()
    trace(
        "native.context.open.start",
        n_ctx=effective_n_ctx,
        n_batch=effective_n_batch,
        n_threads=n_threads,
    )
    with model.open_context(
        n_ctx=effective_n_ctx,
        n_batch=effective_n_batch,
        n_threads=n_threads,
    ) as context:
        trace(
            "native.context.open.done",
            context_size=context.context_size,
            elapsed_ms=round((time.monotonic() - context_started) * 1000),
        )
        prefill_started = time.monotonic()
        trace("native.prefill.start", prompt_tokens=len(prompt_tokens))
        context.prefill(prompt_tokens)
        trace(
            "native.prefill.done",
            elapsed_ms=round((time.monotonic() - prefill_started) * 1000),
            token_count=context.token_count,
        )
        generation_started = time.monotonic()

        def emit_candidate(*, final_utf8: bool, hold_stop_prefix: bool) -> None:
            nonlocal candidate_offset
            if on_candidate is None or candidate_decoder is None:
                return

            safe_end = len(output)
            if hold_stop_prefix:
                safe_end -= _pending_stop_prefix_length(output, encoded_stops)
            raw = bytes(output[candidate_offset:safe_end])
            candidate_offset = safe_end
            try:
                candidate = candidate_decoder.decode(raw, final=final_utf8)
            except UnicodeDecodeError as exc:
                raise NativeRuntimeError(
                    "Native generation produced invalid UTF-8 output bytes."
                ) from exc
            if candidate:
                on_candidate(candidate)

        def finish(reason: NativeGenerationStop, *, final_utf8: bool = False) -> NativeGenerationResult:
            generation_ms = round((time.monotonic() - generation_started) * 1000)
            text = _decode_complete_utf8(bytes(output), final=final_utf8)
            emit_candidate(final_utf8=final_utf8, hold_stop_prefix=False)
            trace(
                "native.generation.done",
                elapsed_ms=generation_ms,
                first_token_ms=first_token_ms if first_token_ms is not None else "none",
                generated_tokens=generated,
                tokens_per_second=(round(generated * 1000 / generation_ms, 2) if generation_ms else 0),
                stop_reason=reason,
            )
            return NativeGenerationResult(
                text=text,
                generated_tokens=generated,
                output_bytes=len(output),
                stop_reason=reason,
                first_token_ms=first_token_ms,
                generation_ms=generation_ms,
            )

        while generated < max_tokens:
            if cancel_check is not None and cancel_check():
                return finish("cancelled")

            # Sampling a token that cannot be appended would leave no valid path to
            # refresh logits, so stop before sampling when the context is full.
            if context.token_count >= context.context_size:
                return finish("context_limit")

            sample = context.sample_greedy()
            if sample.is_eog:
                return finish("eog", final_utf8=True)

            piece = model.token_to_piece(sample.token_id, render_special=False)
            if len(output) + len(piece) > max_output_bytes:
                return finish("output_limit")

            previous_output_bytes = len(output)
            output.extend(piece)
            context.decode_sampled(sample.token_id)
            generated += 1
            if generated == 1:
                first_token_ms = round((time.monotonic() - generation_started) * 1000)
                trace(
                    "native.first_token",
                    elapsed_ms=first_token_ms,
                    total_context_tokens=context.token_count,
                )

            if longest_encoded_stop:
                search_start = max(0, previous_output_bytes - longest_encoded_stop + 1)
                stop_offset = _first_stop_offset(
                    output,
                    encoded_stops,
                    search_start=search_start,
                )
                if stop_offset is not None:
                    del output[stop_offset:]
                    return finish("stop_sequence")

            emit_candidate(final_utf8=False, hold_stop_prefix=True)

        return finish("max_tokens")
