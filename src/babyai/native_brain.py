from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path

from .llm import LLMError, LLMProvider
from .native_generation import generate_greedy
from .native_runtime import NativeRuntimeError, NativeRuntimeLoader


_RESPONSE_BLOCK = re.compile(r"```json\s*(\{.*?\})\s*```", re.DOTALL | re.IGNORECASE)
_THINK_BLOCK = re.compile(r"<think>.*?</think>\s*", re.DOTALL | re.IGNORECASE)
_INTERNAL_REASONING_TAIL = re.compile(
    r"(?im)^(?:okay,\s*)?(?:"
    r"the user (?:asked|said|wants|is asking|wrote)|"
    r"(?:i|we) (?:should|need(?: to)?) (?:answer|respond|reply)|"
    r"(?:let(?:'|’)s|let us) (?:craft|answer|respond|reply)"
    r")\b"
)
_PARENTHESISED_ASCII_LINE = re.compile(r"^\([^()\n]*[A-Za-z][^()\n]*\)$")
_TRAILING_ASCII_PARENTHESIS = re.compile(
    r"\s+\((?=[^()\n]*[A-Za-z])(?=(?:[^A-Za-z()\n]*[A-Za-z]+){4,})[^()\n]*\)\s*$"
)
_NATIVE_STOP_SEQUENCES = (
    "\n\nUSER:",
    "\nUSER:",
    " USER:",
    "\n\nUser:",
    "\nUser:",
    " User:",
    "\n\nuser:",
    "\nuser:",
    " user:",
    "\n\nBABYAI:",
    "\nBABYAI:",
    " BABYAI:",
    "\n\nBabyAI:",
    "\nBabyAI:",
    " BabyAI:",
    "\n\nbabyai:",
    "\nbabyai:",
    " babyai:",
)
_TRANSLATION_REQUEST_MARKERS = (
    "translate",
    "translation",
    "bilingual",
    "переведи",
    "перевод",
    "на английск",
    "на русском",
    "двух язы",
)


def _prepare_native_prompt(prompt: str) -> str:
    """Ask Qwen3-class local models for a short non-thinking assistant turn.

    `/no_think` is a Qwen3 soft switch. Keeping it at the end makes it the most
    recent thinking-mode instruction without changing PRIMUS' provider-neutral prompt.
    The explicit assistant cue also discourages continuation as another user turn.
    """

    return (
        prompt.rstrip()
        + "\n\n/no_think"
        + "\nAnswer directly in the user's language. Do not reveal reasoning. "
        + "Do not add a translation unless the user requested one. "
        + "Do not wrap a normal answer in JSON. "
        + "Return exactly one assistant turn and stop after that answer. "
        + "Never continue the transcript by writing USER:, User:, BABYAI:, or BabyAI:."
        + "\n\nBABYAI:"
    )


def _requests_translation(prompt: str) -> bool:
    latest_user_message = prompt.rsplit("\n\nUSER:", 1)[-1].casefold()
    return any(marker in latest_user_message for marker in _TRANSLATION_REQUEST_MARKERS)


def _strip_internal_reasoning_tail(text: str, *, allow_translation: bool = False) -> str:
    """Remove untagged scratchpad text emitted after an already complete answer."""

    match = _INTERNAL_REASONING_TAIL.search(text)
    if match is not None and text[: match.start()].strip():
        text = text[: match.start()].rstrip()

    lines = text.splitlines()
    visible: list[str] = []
    visible_text = ""
    for line in lines:
        stripped = line.strip()
        if (
            not allow_translation
            and stripped
            and _PARENTHESISED_ASCII_LINE.fullmatch(stripped)
            and re.search(r"[А-Яа-яЁё]", visible_text)
        ):
            continue
        if not allow_translation and re.search(r"[А-Яа-яЁё]", stripped):
            without_translation = _TRAILING_ASCII_PARENTHESIS.sub("", stripped).rstrip()
            if without_translation != stripped:
                line = without_translation
        visible.append(line)
        if stripped:
            visible_text += " " + stripped
    return "\n".join(visible).strip()


def _normalise_native_reply(text: str, *, allow_translation: bool = False) -> str:
    """Return the user-facing answer while preserving exact tool-call JSON."""

    cleaned = _THINK_BLOCK.sub("", text).strip()
    if cleaned.upper().startswith("BABYAI:"):
        cleaned = cleaned[7:].lstrip()

    # Qwen3 can still emit a fenced response wrapper when prompted by older context.
    # Prefer the last valid wrapper because earlier text may contain model reasoning.
    for match in reversed(list(_RESPONSE_BLOCK.finditer(cleaned))):
        try:
            payload = json.loads(match.group(1))
        except json.JSONDecodeError:
            continue
        if isinstance(payload, dict):
            response = payload.get("response")
            if isinstance(response, str) and response.strip():
                return response.strip()
            # Preserve a real tool call in the exact compact JSON form AgentExecutor
            # already understands instead of turning it into prose.
            if isinstance(payload.get("tool"), str) and isinstance(payload.get("arguments"), dict):
                return json.dumps(payload, ensure_ascii=False, separators=(",", ":"))

    # Also accept a whole-output JSON object without a code fence.
    try:
        payload = json.loads(cleaned)
    except json.JSONDecodeError:
        payload = None
    if isinstance(payload, dict):
        response = payload.get("response")
        if isinstance(response, str) and response.strip():
            return response.strip()
        if isinstance(payload.get("tool"), str) and isinstance(payload.get("arguments"), dict):
            return json.dumps(payload, ensure_ascii=False, separators=(",", ":"))

    return _strip_internal_reasoning_tail(cleaned, allow_translation=allow_translation)


@dataclass(slots=True)
class NativeBrainProvider(LLMProvider):
    """Run bounded GGUF inference in-process through BabyAI's stable native shim.

    Each generate call owns a fresh runtime/model/context lifetime. Native chat keeps
    the first correctness-first ownership model, but uses a shorter non-thinking turn
    and managed stop delimiters so simple desktop messages do not waste CPU generating
    hidden reasoning or invented follow-up turns.
    """

    model_path: Path
    runtime_path: Path
    max_tokens: int = 128
    max_output_bytes: int = 1_048_576
    n_ctx: int = 4096
    n_batch: int = 4096
    n_threads: int = 0
    n_gpu_layers: int = 0

    def generate(self, prompt: str) -> str:
        native_prompt = _prepare_native_prompt(prompt)
        try:
            with NativeRuntimeLoader(self.runtime_path).open_runtime() as runtime:
                with runtime.open_model(self.model_path, n_gpu_layers=self.n_gpu_layers) as model:
                    result = generate_greedy(
                        model,
                        native_prompt,
                        max_tokens=self.max_tokens,
                        max_output_bytes=self.max_output_bytes,
                        n_ctx=self.n_ctx,
                        n_batch=self.n_batch,
                        n_threads=self.n_threads,
                        stop_sequences=_NATIVE_STOP_SEQUENCES,
                        fit_context_to_prompt=True,
                    )
        except NativeRuntimeError as exc:
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
        return text
