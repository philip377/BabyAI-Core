from __future__ import annotations

import codecs
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from typing import Literal

from .native_runtime import MAX_NATIVE_TEXT_BYTES, NativeModelHandle, NativeRuntimeError


MAX_NATIVE_GENERATION_TOKENS = 4096

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
    stop_sequences: Sequence[str] = (),
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
    if isinstance(stop_sequences, (str, bytes)) or not isinstance(stop_sequences, Sequence):
        raise NativeRuntimeError("Native generation stop_sequences must be a sequence of strings.")
    encoded_stops = _encode_stop_sequences(stop_sequences)

    prompt_tokens = model.tokenize(prompt, add_special=True, parse_special=True)
    if not prompt_tokens:
        raise NativeRuntimeError("Native generation prompt tokenized to an empty sequence.")

    output = bytearray()
    generated = 0

    with model.open_context(n_ctx=n_ctx, n_batch=n_batch, n_threads=n_threads) as context:
        context.prefill(prompt_tokens)

        while generated < max_tokens:
            if cancel_check is not None and cancel_check():
                return NativeGenerationResult(
                    text=_decode_complete_utf8(bytes(output), final=False),
                    generated_tokens=generated,
                    output_bytes=len(output),
                    stop_reason="cancelled",
                )

            # Sampling a token that cannot be appended would leave no valid path to
            # refresh logits, so stop before sampling when the context is full.
            if context.token_count >= context.context_size:
                return NativeGenerationResult(
                    text=_decode_complete_utf8(bytes(output), final=False),
                    generated_tokens=generated,
                    output_bytes=len(output),
                    stop_reason="context_limit",
                )

            sample = context.sample_greedy()
            if sample.is_eog:
                return NativeGenerationResult(
                    text=_decode_complete_utf8(bytes(output), final=True),
                    generated_tokens=generated,
                    output_bytes=len(output),
                    stop_reason="eog",
                )

            piece = model.token_to_piece(sample.token_id, render_special=False)
            if len(output) + len(piece) > max_output_bytes:
                return NativeGenerationResult(
                    text=_decode_complete_utf8(bytes(output), final=False),
                    generated_tokens=generated,
                    output_bytes=len(output),
                    stop_reason="output_limit",
                )

            output.extend(piece)
            context.decode_sampled(sample.token_id)
            generated += 1

            for stop in encoded_stops:
                if output.endswith(stop):
                    del output[-len(stop) :]
                    return NativeGenerationResult(
                        text=_decode_complete_utf8(bytes(output), final=False),
                        generated_tokens=generated,
                        output_bytes=len(output),
                        stop_reason="stop_sequence",
                    )

    return NativeGenerationResult(
        text=_decode_complete_utf8(bytes(output), final=False),
        generated_tokens=generated,
        output_bytes=len(output),
        stop_reason="max_tokens",
    )
