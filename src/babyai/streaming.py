from __future__ import annotations

import re
import secrets
from dataclasses import dataclass


_LEADING_HIDDEN_TAGS = ("think", "analysis", "reasoning")
_INTERNAL_MARKERS = (
    "```",
    "<think",
    "</think",
    "<analysis",
    "</analysis",
    "<reasoning",
    "</reasoning",
    "<babyai-visible-",
    "</babyai-visible-",
    '"tool"',
    '"arguments"',
    '"response"',
    "available tools:",
    "tool:",
    "result:",
    "/no_think",
    "\nuser:",
    "\nbabyai:",
    "streaming display contract:",
    "the marker must be the first output",
    "never put the marker before json",
    "emit exactly one assistant turn",
    "do not explain or repeat this contract",
    "answer directly in the user's language",
    "do not reveal reasoning",
    "do not add a translation unless",
    "do not wrap a normal answer in json",
    "return exactly one assistant turn",
    "never continue the transcript by writing",
)
_INTERNAL_REASONING = re.compile(
    r"(?im)^(?:"
    r"okay,\s*(?:"
    r"the user\b|"
    r"(?:i|we) (?:should|need(?: to)?) "
    r"(?:answer|respond|reply|figure out|determine|decide|understand|analy[sz]e|consider)|"
    r"let me (?:try to )?(?:figure out|determine|decide|understand|analy[sz]e|consider)"
    r")|"
    r"the user\b|"
    r"(?:i|we) (?:should|need(?: to)?) (?:answer|respond|reply)|"
    r"(?:let(?:'|’)s|let us) (?:craft|answer|respond|reply)|"
    r"let me (?:start by |try to )?(?:understand|analy[sz]e|consider|figure out|determine|decide)"
    r")\b"
)
_SYNTHETIC_ROLE_CONTINUATION = re.compile(
    r"(?:^|[\r\n\t ])(?:USER|User|BABYAI|BabyAI)\s*:"
)
_UNSOLICITED_TRANSLATION_START = re.compile(r"\s+\((?=[^()\n]*[A-Za-z])")
_VISIBLE_MARKER_OPEN_PREFIX = "<babyai-visible-"
_VISIBLE_MARKER_PREFIXES = (_VISIBLE_MARKER_OPEN_PREFIX, "</babyai-visible-")


class StreamingSafetyError(ValueError):
    pass


@dataclass(frozen=True, slots=True)
class StreamMetrics:
    native_first_token_ms: int | None = None
    generation_ms: int | None = None
    generated_tokens: int | None = None
    stop_reason: str | None = None
    model_calls: int = 0


@dataclass(frozen=True, slots=True)
class StreamReply:
    reply: str
    metrics: StreamMetrics = StreamMetrics()


def new_visible_marker() -> str:
    return f"<babyai-visible-{secrets.token_hex(16)}>"


def with_visible_marker_contract(prompt: str, marker: str) -> str:
    return (
        prompt
        + "\n\nStreaming display contract: if and only if this generation is the final "
        + "natural-language answer for the user, begin the answer with exactly "
        + marker
        + ". The marker must be the first output after any optional <think> block. "
        + "Never put the marker before JSON, a tool call, reasoning, protocol data, or a code fence. "
        + "Emit exactly one assistant turn; never continue with USER: or BABYAI: role labels. "
        + "Do not explain or repeat this contract."
    )


class VisibleTextGate:
    """Publish only a conservative, monotonic prefix of an untrusted model draft.

    The canonical completed PRIMUS reply remains authoritative. Suspicious output is
    quarantined and safely degrades to a completed response rather than being exposed.
    """

    _HOLD_BACK = 64

    def __init__(self, *, marker: str | None = None, tool_names: tuple[str, ...] = ()) -> None:
        self._raw = ""
        self._visible = ""
        self._emitted = ""
        self._blocked = False
        self._opened = False
        self._invalid = False
        self._safe_prefix_before_block: str | None = None
        self._marker = marker
        self._tool_names = tuple(name.casefold() for name in tool_names)

    @property
    def emitted(self) -> str:
        return self._emitted

    @property
    def opened(self) -> bool:
        return self._opened

    @property
    def invalid(self) -> bool:
        return self._invalid

    def feed(self, chunk: str) -> str:
        if not isinstance(chunk, str):
            raise TypeError("Streaming candidate chunks must be strings")
        if not chunk or self._blocked:
            return ""

        self._raw += chunk
        if not self._opened:
            if self._marker is None:
                return ""
            prefix = self._after_hidden_prefix(self._raw)
            if prefix is None or self._marker.startswith(prefix):
                return ""
            if not prefix.startswith(self._marker):
                self._blocked = True
                return ""
            # Resident native owns the nonce and prefixes it out-of-band before
            # any model token.  A previously adopted thinking-model GGUF can then
            # start its otherwise valid answer with <think>...</think>.  Apply the
            # same leading-hidden-block boundary on the model side of the nonce;
            # do not open the visible channel until actual answer text follows.
            # Incomplete tags remain buffered, and malformed/non-leading protocol
            # text still reaches _candidate() and fails closed as before.
            visible = self._after_hidden_prefix(prefix[len(self._marker) :])
            if visible is None or not visible:
                return ""
            self._opened = True
            self._visible = visible
        else:
            self._visible += chunk

        candidate = self._candidate(self._visible)
        if candidate is None:
            return ""
        if not candidate.startswith(self._emitted):
            self._invalid = True
            self._blocked = True
            return ""
        commit_end = max(len(self._emitted), len(candidate) - self._HOLD_BACK)
        boundary = max(
            candidate.rfind(" ", len(self._emitted), commit_end + 1),
            candidate.rfind("\n", len(self._emitted), commit_end + 1),
        )
        if boundary < len(self._emitted):
            return ""
        delta = candidate[len(self._emitted) : boundary + 1]
        self._emitted += delta
        return delta

    def strip_marker(self, canonical: str) -> str:
        """Return only a completed reply that is safe even without progressive authorization.

        Missing visible markers are allowed as a compatibility fallback, but the completed
        canonical reply must still pass the same display boundary. This prevents prompt or
        streaming-contract echoes from being accepted through ``done.reply`` simply because
        no progressive channel was opened.
        """

        if not isinstance(canonical, str):
            raise StreamingSafetyError("Streaming completed reply must be text")
        if self._marker is None:
            return self._validated_completed_text(canonical)

        text = self._after_hidden_prefix(canonical)
        if text is not None and text.startswith(self._marker):
            body = text[len(self._marker) :].lstrip()
            folded = canonical.casefold()
            body_folded = body.casefold()
            if (
                folded.count(_VISIBLE_MARKER_OPEN_PREFIX) != 1
                or any(prefix in body_folded for prefix in _VISIBLE_MARKER_PREFIXES)
            ):
                raise StreamingSafetyError("Streaming response marker validation failed")
            return self._validated_completed_text(body)
        folded = canonical.casefold()
        if any(prefix in folded for prefix in _VISIBLE_MARKER_PREFIXES):
            raise StreamingSafetyError("Streaming response marker validation failed")
        return self._validated_completed_text(canonical)

    def _validated_completed_text(self, text: str) -> str:
        probe = VisibleTextGate(tool_names=tuple(self._tool_names))
        safe = probe._candidate(text)
        if safe is None or probe._blocked or safe.strip() != text.strip():
            raise StreamingSafetyError("Streaming completed reply failed safety validation")
        return text

    def validated_open_body(self, canonical: str) -> str | None:
        """Return a safe canonical body only when it retains this gate's nonce."""

        if not self._opened or self._marker is None:
            return None
        text = self._after_hidden_prefix(canonical)
        if text is None or not text.startswith(self._marker):
            return None
        body = text[len(self._marker) :].lstrip()
        try:
            safe_body = self._validated_completed_text(body)
        except StreamingSafetyError:
            return None
        if not safe_body.startswith(self._emitted):
            return None
        if self._invalid:
            # A local model may append an unsafe scratchpad after an already complete
            # answer. The streaming gate quarantines that suffix immediately, while
            # the provider's canonical normalizer removes it. Recover only when the
            # completed body is exactly the safe prefix observed before the block;
            # arbitrary rewrites, tool calls and protocol tails remain rejected.
            safe_prefix = self._safe_prefix_before_block
            if safe_prefix is None or safe_body.strip() != safe_prefix.strip():
                return None
        return safe_body

    def finish(self, canonical: str) -> str:
        """Emit only the unseen suffix of an already validated canonical reply."""

        if not isinstance(canonical, str) or not canonical.startswith(self._emitted):
            return ""
        probe = VisibleTextGate(tool_names=tuple(self._tool_names))
        safe = probe._candidate(canonical)
        if safe is None or probe._blocked or safe.strip() != canonical.strip():
            return ""
        delta = canonical[len(self._emitted) :]
        self._emitted += delta
        return delta

    @staticmethod
    def _after_hidden_prefix(value: str) -> str | None:
        text = value.lstrip()
        lower = text.casefold()

        while text:
            removed = False
            for tag in _LEADING_HIDDEN_TAGS:
                opening = f"<{tag}>"
                closing = f"</{tag}>"
                if opening.startswith(lower):
                    return None
                if lower.startswith(opening):
                    end = lower.find(closing, len(opening))
                    if end < 0:
                        return None
                    text = text[end + len(closing) :].lstrip()
                    lower = text.casefold()
                    removed = True
                    break
            if not removed:
                break

        return text

    def _candidate(self, value: str) -> str | None:
        text = value
        lower = text.casefold()

        if not text:
            return ""
        if lower.startswith("babyai:"):
            text = text[len("babyai:") :].lstrip()
            lower = text.casefold()

        if text[:1] in "{[" or lower.startswith("```"):
            self._blocked = True
            self._invalid = self._opened
            return None

        unsafe_positions: list[int] = []
        for marker in _INTERNAL_MARKERS + self._tool_names:
            position = lower.find(marker)
            if position >= 0:
                unsafe_positions.append(position)
        for char in ("{", "["):
            position = text.find(char)
            if position >= 0:
                unsafe_positions.append(position)
        reasoning = _INTERNAL_REASONING.search(text)
        if reasoning is not None:
            unsafe_positions.append(reasoning.start())
        role_continuation = _SYNTHETIC_ROLE_CONTINUATION.search(text)
        if role_continuation is not None:
            unsafe_positions.append(role_continuation.start())
        if re.search(r"[А-Яа-яЁё]", text):
            translation = _UNSOLICITED_TRANSLATION_START.search(text)
            if translation is not None:
                unsafe_positions.append(translation.start())

        if unsafe_positions:
            self._blocked = True
            safe_prefix = text[: min(unsafe_positions)].rstrip()
            if self._opened:
                self._invalid = True
                self._safe_prefix_before_block = safe_prefix
                return None
            text = safe_prefix
        return text
