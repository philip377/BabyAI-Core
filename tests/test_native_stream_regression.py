from __future__ import annotations

from pathlib import Path

from babyai.native_generation import NativeGenerationResult
from babyai.resident_native_brain import ResidentNativeBrainProvider
from babyai.streaming import VisibleTextGate, new_visible_marker, with_visible_marker_contract


def test_native_shim_keeps_repetition_sampler_for_the_context() -> None:
    source = Path("native/BabyAI.NativeBridge/src/babyai_native.cpp").read_text(encoding="utf-8")

    assert "llama_sampler * sampler = nullptr;" in source
    assert "llama_sampler_init_top_k(k_sampling_top_k)" in source
    assert "llama_sampler_init_penalties(" in source
    assert "k_repeat_penalty_last_n" in source
    assert "k_repeat_penalty = 1.12f" in source
    assert "llama_sampler_sample(context->sampler, context->handle, -1)" in source
    assert "llama_sampler_free(context->sampler);" in source


def test_resident_native_prefills_visible_nonce_and_streams_before_completion(monkeypatch) -> None:
    provider = ResidentNativeBrainProvider(
        model_path=Path("model.gguf"),
        runtime_path=Path("babyai_native.dll"),
    )
    fake_model = object()
    monkeypatch.setattr(
        ResidentNativeBrainProvider,
        "_ensure_model",
        lambda self: fake_model,
    )

    marker = new_visible_marker()
    prompt = with_visible_marker_contract("USER: Расскажи подробнее", marker)
    answer = "Это безопасный пользовательский ответ, который приходит постепенно. " * 6
    gate = VisibleTextGate(marker=marker)
    visible_deltas: list[str] = []
    captured: dict[str, object] = {}

    def accept(chunk: str) -> None:
        delta = gate.feed(chunk)
        if delta:
            visible_deltas.append(delta)

    def fake_generate(model, native_prompt: str, **kwargs) -> NativeGenerationResult:
        assert model is fake_model
        captured["prompt"] = native_prompt
        sink = kwargs["on_candidate"]
        assert callable(sink)
        for index in range(0, len(answer), 19):
            sink(answer[index : index + 19])
            if visible_deltas:
                captured["streamed_before_return"] = True
        return NativeGenerationResult(
            text=answer,
            generated_tokens=42,
            output_bytes=len(answer.encode("utf-8")),
            stop_reason="eog",
            first_token_ms=7,
            generation_ms=80,
        )

    monkeypatch.setattr("babyai.resident_native_brain.generate_greedy", fake_generate)

    result = provider.generate_stream(prompt, accept)

    assert str(captured["prompt"]).endswith("\n\nBABYAI:" + marker)
    assert captured.get("streamed_before_return") is True
    assert gate.opened is True
    body = gate.validated_open_body(result.text)
    assert body == answer
    assert "".join(visible_deltas) + gate.finish(body) == answer
    assert marker not in "".join(visible_deltas)
    assert result.first_token_ms == 7
    assert result.generated_tokens == 42
