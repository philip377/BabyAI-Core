from __future__ import annotations

from dataclasses import dataclass

import pytest

from babyai.native_generation import MAX_NATIVE_GENERATION_TOKENS, generate_greedy
from babyai.native_runtime import NativeRuntimeError, NativeSample


@dataclass
class _FakeContext:
    samples: list[NativeSample]
    context_size: int = 32
    token_count: int = 0

    def __post_init__(self):
        self.prefilled: tuple[int, ...] | None = None
        self.decoded: list[int] = []
        self.closed = False

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, tb):
        self.closed = True

    def prefill(self, tokens):
        self.prefilled = tuple(tokens)
        self.token_count = len(tokens)
        return self.token_count

    def sample_greedy(self):
        if not self.samples:
            raise AssertionError("test exhausted fake samples")
        return self.samples.pop(0)

    def decode_sampled(self, token_id):
        self.decoded.append(token_id)
        self.token_count += 1
        return self.token_count


class _FakeModel:
    def __init__(self, samples, pieces, *, prompt_tokens=(1, 2), context_size=32):
        self.samples = list(samples)
        self.pieces = dict(pieces)
        self.prompt_tokens = list(prompt_tokens)
        self.context = _FakeContext(self.samples, context_size=context_size)
        self.tokenize_calls = []
        self.context_args = None

    def tokenize(self, text, *, add_special, parse_special):
        self.tokenize_calls.append((text, add_special, parse_special))
        return list(self.prompt_tokens)

    def token_to_piece(self, token_id, *, render_special):
        assert render_special is False
        return self.pieces[token_id]

    def open_context(self, *, n_ctx, n_batch, n_threads):
        self.context_args = (n_ctx, n_batch, n_threads)
        return self.context


def test_generation_loops_sample_piece_decode_until_eog_and_combines_utf8():
    model = _FakeModel(
        [
            NativeSample(10, False),
            NativeSample(11, False),
            NativeSample(12, False),
            NativeSample(99, True),
        ],
        {10: b"A\xd0", 11: b"\x9f", 12: b"!"},
    )

    result = generate_greedy(model, "hello", max_tokens=8, n_ctx=64, n_batch=16, n_threads=3)

    assert result.text == "AП!"
    assert result.generated_tokens == 3
    assert result.output_bytes == 4
    assert result.stop_reason == "eog"
    assert model.context.decoded == [10, 11, 12]
    assert model.context.prefilled == (1, 2)
    assert model.context_args == (64, 16, 3)
    assert model.context.closed is True
    assert model.tokenize_calls == [("hello", True, True)]


def test_generation_stops_on_managed_delimiter_and_removes_it():
    model = _FakeModel(
        [
            NativeSample(10, False),
            NativeSample(11, False),
            NativeSample(12, False),
            NativeSample(13, False),
        ],
        {10: b"hello", 11: b"\n", 12: b"USER:", 13: b"should-not-run"},
    )

    result = generate_greedy(model, "prompt", stop_sequences=("\nUSER:",))

    assert result.text == "hello"
    assert result.stop_reason == "stop_sequence"
    assert result.generated_tokens == 3
    assert result.output_bytes == 5
    assert model.context.decoded == [10, 11, 12]
    assert model.samples == [NativeSample(13, False)]


def test_generation_stop_sequence_can_span_token_pieces():
    model = _FakeModel(
        [NativeSample(10, False), NativeSample(11, False), NativeSample(12, False)],
        {10: b"answer\nUS", 11: b"ER:", 12: b"unused"},
    )

    result = generate_greedy(model, "prompt", stop_sequences=("\nUSER:",))

    assert result.text == "answer"
    assert result.stop_reason == "stop_sequence"
    assert model.context.decoded == [10, 11]


def test_generation_max_tokens_is_hard_bound_and_omits_incomplete_utf8_suffix():
    model = _FakeModel(
        [NativeSample(10, False), NativeSample(11, False)],
        {10: b"ok\xd0", 11: b"\x9f"},
    )

    result = generate_greedy(model, "prompt", max_tokens=1)

    assert result.text == "ok"
    assert result.generated_tokens == 1
    assert result.output_bytes == 3
    assert result.stop_reason == "max_tokens"
    assert model.context.decoded == [10]


def test_generation_stops_before_sampling_when_context_is_full():
    model = _FakeModel(
        [NativeSample(10, False)],
        {10: b"unused"},
        prompt_tokens=(1, 2),
        context_size=2,
    )

    result = generate_greedy(model, "prompt")

    assert result.stop_reason == "context_limit"
    assert result.generated_tokens == 0
    assert model.samples == [NativeSample(10, False)]
    assert model.context.decoded == []


def test_generation_output_limit_does_not_append_rejected_token():
    model = _FakeModel(
        [NativeSample(10, False), NativeSample(11, False)],
        {10: b"ab", 11: b"cde"},
    )

    result = generate_greedy(model, "prompt", max_output_bytes=4)

    assert result.text == "ab"
    assert result.output_bytes == 2
    assert result.generated_tokens == 1
    assert result.stop_reason == "output_limit"
    assert model.context.decoded == [10]


def test_generation_cancellation_is_cooperative_at_token_boundaries():
    checks = iter((False, True))
    model = _FakeModel(
        [NativeSample(10, False), NativeSample(11, False)],
        {10: b"done", 11: b"unused"},
    )

    result = generate_greedy(model, "prompt", cancel_check=lambda: next(checks))

    assert result.text == "done"
    assert result.generated_tokens == 1
    assert result.stop_reason == "cancelled"
    assert model.context.decoded == [10]


def test_generation_rejects_invalid_utf8_when_eog_claims_output_is_complete():
    model = _FakeModel(
        [NativeSample(10, False), NativeSample(99, True)],
        {10: b"\xd0"},
    )

    with pytest.raises(NativeRuntimeError, match="invalid UTF-8"):
        generate_greedy(model, "prompt")


def test_generation_rejects_unbounded_or_invalid_limits_before_native_work():
    model = _FakeModel([], {})

    with pytest.raises(NativeRuntimeError, match="max_tokens"):
        generate_greedy(model, "prompt", max_tokens=0)
    with pytest.raises(NativeRuntimeError, match="safety limit"):
        generate_greedy(model, "prompt", max_tokens=MAX_NATIVE_GENERATION_TOKENS + 1)
    with pytest.raises(NativeRuntimeError, match="max_output_bytes"):
        generate_greedy(model, "prompt", max_output_bytes=0)
    with pytest.raises(NativeRuntimeError, match="n_ctx"):
        generate_greedy(model, "prompt", n_ctx=-1)
    with pytest.raises(NativeRuntimeError, match="stop sequences"):
        generate_greedy(model, "prompt", stop_sequences=("",))

    assert model.tokenize_calls == []


def test_generation_rejects_empty_prompt_tokenization():
    model = _FakeModel([], {}, prompt_tokens=())

    with pytest.raises(NativeRuntimeError, match="empty sequence"):
        generate_greedy(model, "")


def test_generation_can_right_size_context_and_batch_to_the_prompt():
    model = _FakeModel(
        [NativeSample(99, True)],
        {},
        prompt_tokens=range(1405),
        context_size=1792,
    )

    result = generate_greedy(
        model,
        "representative prompt",
        max_tokens=128,
        n_ctx=4096,
        n_batch=4096,
        n_threads=4,
        fit_context_to_prompt=True,
    )

    assert result.stop_reason == "eog"
    assert model.context_args == (1536, 1536, 4)


def test_generation_keeps_configured_limits_when_prompt_does_not_fit():
    model = _FakeModel(
        [NativeSample(99, True)],
        {},
        prompt_tokens=range(4000),
        context_size=4096,
    )

    result = generate_greedy(
        model,
        "oversized prompt",
        max_tokens=128,
        n_ctx=4096,
        n_batch=2048,
        fit_context_to_prompt=True,
    )

    assert result.stop_reason == "eog"
    assert model.context_args == (4096, 2048, 0)
