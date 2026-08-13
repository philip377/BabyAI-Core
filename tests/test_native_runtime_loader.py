from __future__ import annotations

import ctypes

import pytest

from babyai.native_runtime import (
    BABYAI_NATIVE_ABI_VERSION,
    MAX_NATIVE_TOKEN_COUNT,
    REQUIRED_BABYAI_SYMBOLS,
    NativeRuntimeError,
    NativeRuntimeLoader,
)


class _FakeFunction:
    def __init__(self, value=0, callback=None):
        self.value = value
        self.callback = callback
        self.argtypes = None
        self.restype = None

    def __call__(self, *args):
        if self.callback is not None:
            return self.callback(*args)
        return self.value


class _FakeLibrary:
    pass


def _compatible_library(abi_version: int = BABYAI_NATIVE_ABI_VERSION):
    library = _FakeLibrary()
    for name in REQUIRED_BABYAI_SYMBOLS:
        setattr(library, name, _FakeFunction())
    library.babyai_native_abi_version = _FakeFunction(abi_version)
    return library


def _lifecycle_library(
    *,
    model_result: int = 0,
    context_result: int = 0,
    tokenize_result: int | None = None,
    prefill_result: int = 0,
    sample_result: int = 0,
    sampled_token: int = 404,
    sampled_eog: bool = False,
    token_ids: tuple[int, ...] = (101, 202, 303),
    token_required: int | None = None,
    last_error: bytes = b"",
    context_size: int = 4096,
    batch_size: int = 512,
):
    calls: list[object] = []
    library = _compatible_library()
    context_state = {"tokens": 0, "sampled": False}

    def runtime_create(out_runtime):
        calls.append("runtime_create")
        out_runtime._obj.value = 0x101
        return 0

    def runtime_destroy(runtime):
        calls.append("runtime_destroy")

    def model_open(runtime, path, n_gpu_layers, out_model):
        decoded = path.decode("utf-8")
        calls.append(("model_open", decoded, int(n_gpu_layers)))
        if model_result == 0:
            out_model._obj.value = 0x201
        return model_result

    def model_close(model):
        calls.append(("model_close", int(model.value or 0)))

    def model_tokenize(
        runtime,
        model,
        text,
        text_len,
        add_special,
        parse_special,
        tokens_out,
        capacity,
        out_count,
    ):
        raw = bytes(text[: int(text_len)])
        decoded = raw.decode("utf-8")
        calls.append(("tokenize", decoded, bool(add_special), bool(parse_special), int(capacity)))
        required = token_required if token_required is not None else len(token_ids)
        out_count._obj.value = required
        if tokenize_result is not None:
            return tokenize_result
        if tokens_out is None or int(capacity) < required:
            return 6
        for index, token in enumerate(token_ids):
            tokens_out[index] = token
        out_count._obj.value = len(token_ids)
        return 0

    def context_create(runtime, model, n_ctx, n_batch, n_threads, out_context):
        calls.append(("context_create", int(n_ctx), int(n_batch), int(n_threads)))
        if context_result == 0:
            out_context._obj.value = 0x301 + len(calls)
            context_state["tokens"] = 0
            context_state["sampled"] = False
        return context_result

    def context_destroy(context):
        calls.append(("context_destroy", int(context.value or 0)))

    def context_prefill(runtime, context, tokens, token_count):
        values = tuple(int(tokens[index]) for index in range(int(token_count)))
        calls.append(("prefill", values))
        if context_state["tokens"]:
            return 9
        if prefill_result != 0:
            return prefill_result
        context_state["tokens"] = int(token_count)
        return 0

    def context_sample(runtime, context, out_token, out_is_eog):
        calls.append("sample_greedy")
        if context_state["tokens"] <= 0:
            return 11
        if context_state["sampled"]:
            return 12
        if sample_result != 0:
            return sample_result
        out_token._obj.value = sampled_token
        out_is_eog._obj.value = 1 if sampled_eog else 0
        context_state["sampled"] = True
        return 0

    library.babyai_native_runtime_create = _FakeFunction(callback=runtime_create)
    library.babyai_native_runtime_destroy = _FakeFunction(callback=runtime_destroy)
    library.babyai_native_model_open = _FakeFunction(callback=model_open)
    library.babyai_native_model_close = _FakeFunction(callback=model_close)
    library.babyai_native_model_tokenize = _FakeFunction(callback=model_tokenize)
    library.babyai_native_context_create = _FakeFunction(callback=context_create)
    library.babyai_native_context_destroy = _FakeFunction(callback=context_destroy)
    library.babyai_native_context_n_ctx = _FakeFunction(value=context_size)
    library.babyai_native_context_n_batch = _FakeFunction(value=batch_size)
    library.babyai_native_context_prefill = _FakeFunction(callback=context_prefill)
    library.babyai_native_context_token_count = _FakeFunction(callback=lambda context: context_state["tokens"])
    library.babyai_native_context_sample_greedy = _FakeFunction(callback=context_sample)
    library.babyai_native_last_error = _FakeFunction(callback=lambda runtime: last_error)
    return library, calls


def _install_fake_runtime(tmp_path, monkeypatch, library):
    runtime = tmp_path / "babyai_native.dll"
    runtime.write_bytes(b"placeholder")
    monkeypatch.setattr("babyai.native_runtime.ctypes.CDLL", lambda path: library)
    return runtime


def test_loader_rejects_missing_runtime_file(tmp_path):
    with pytest.raises(NativeRuntimeError, match="Native runtime library not found"):
        NativeRuntimeLoader(tmp_path / "babyai_native.dll").load()


def test_loader_wraps_dynamic_library_load_error(tmp_path, monkeypatch):
    runtime = tmp_path / "babyai_native.dll"
    runtime.write_bytes(b"placeholder")
    monkeypatch.setattr("babyai.native_runtime.ctypes.CDLL", lambda path: (_ for _ in ()).throw(OSError("bad image")))
    with pytest.raises(NativeRuntimeError, match="Could not load native runtime library"):
        NativeRuntimeLoader(runtime).load()


def test_loader_rejects_library_missing_required_symbols(tmp_path, monkeypatch):
    runtime = tmp_path / "babyai_native.dll"
    runtime.write_bytes(b"placeholder")
    monkeypatch.setattr("babyai.native_runtime.ctypes.CDLL", lambda path: _FakeLibrary())
    with pytest.raises(NativeRuntimeError, match="does not satisfy BabyAI native ABI v5") as exc:
        NativeRuntimeLoader(runtime).load()
    assert "babyai_native_context_sample_greedy" in str(exc.value)


def test_loader_rejects_wrong_abi_version(tmp_path, monkeypatch):
    runtime = tmp_path / "babyai_native.dll"
    runtime.write_bytes(b"placeholder")
    monkeypatch.setattr("babyai.native_runtime.ctypes.CDLL", lambda path: _compatible_library(abi_version=99))
    with pytest.raises(NativeRuntimeError, match="Native runtime ABI mismatch"):
        NativeRuntimeLoader(runtime).load()


def test_loader_configures_sample_abi(tmp_path, monkeypatch):
    library = _compatible_library()
    runtime = _install_fake_runtime(tmp_path, monkeypatch, library)
    handle = NativeRuntimeLoader(runtime).load()
    assert handle.abi_version == BABYAI_NATIVE_ABI_VERSION
    assert library.babyai_native_context_sample_greedy.argtypes == [
        ctypes.c_void_p,
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_int32),
        ctypes.POINTER(ctypes.c_int32),
    ]


def test_model_tokenize_is_two_pass_utf8_and_preserves_flags(tmp_path, monkeypatch):
    library, calls = _lifecycle_library(token_ids=(17, 23, 42))
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        with runtime.open_model(tmp_path / "model.gguf") as model:
            tokens = model.tokenize("Привет, BabyAI", add_special=False, parse_special=True)
    assert tokens == [17, 23, 42]
    token_calls = [call for call in calls if isinstance(call, tuple) and call[0] == "tokenize"]
    assert token_calls == [
        ("tokenize", "Привет, BabyAI", False, True, 0),
        ("tokenize", "Привет, BabyAI", False, True, 3),
    ]


def test_model_tokenize_rejects_unbounded_native_size_before_allocation(tmp_path, monkeypatch):
    library, calls = _lifecycle_library(token_required=MAX_NATIVE_TOKEN_COUNT + 1)
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        with runtime.open_model(tmp_path / "model.gguf") as model:
            with pytest.raises(NativeRuntimeError, match="safety limit"):
                model.tokenize("oversized")
    assert len([call for call in calls if isinstance(call, tuple) and call[0] == "tokenize"]) == 1


def test_context_prefill_commits_bounded_token_ids(tmp_path, monkeypatch):
    library, calls = _lifecycle_library(context_size=1024, batch_size=256)
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        with runtime.open_model(tmp_path / "model.gguf") as model:
            with model.open_context(n_ctx=1024, n_batch=256) as context:
                assert context.prefill([17, 23, 42]) == 3
                assert context.token_count == 3
    assert ("prefill", (17, 23, 42)) in calls


def test_context_prefill_rejects_batch_oversize_before_native_call(tmp_path, monkeypatch):
    library, calls = _lifecycle_library(context_size=16, batch_size=3)
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        with runtime.open_model(tmp_path / "model.gguf") as model:
            with model.open_context() as context:
                with pytest.raises(NativeRuntimeError, match="at most 3"):
                    context.prefill([1, 2, 3, 4])
    assert not any(isinstance(call, tuple) and call[0] == "prefill" for call in calls)


def test_context_sample_requires_prefill_before_native_call(tmp_path, monkeypatch):
    library, calls = _lifecycle_library()
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        with runtime.open_model(tmp_path / "model.gguf") as model:
            with model.open_context() as context:
                with pytest.raises(NativeRuntimeError, match="prefilled"):
                    context.sample_greedy()
    assert "sample_greedy" not in calls


def test_context_sample_greedy_returns_token_and_eog(tmp_path, monkeypatch):
    library, calls = _lifecycle_library(sampled_token=777, sampled_eog=True)
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        with runtime.open_model(tmp_path / "model.gguf") as model:
            with model.open_context() as context:
                context.prefill([1, 2, 3])
                sample = context.sample_greedy()
                assert sample.token_id == 777
                assert sample.is_eog is True
                assert context.token_count == 3
    assert calls.count("sample_greedy") == 1


def test_context_sample_greedy_is_one_shot_until_decode_contract_exists(tmp_path, monkeypatch):
    library, calls = _lifecycle_library(sampled_token=88)
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        with runtime.open_model(tmp_path / "model.gguf") as model:
            with model.open_context() as context:
                context.prefill([1, 2])
                assert context.sample_greedy().token_id == 88
                with pytest.raises(NativeRuntimeError, match="already sampled"):
                    context.sample_greedy()
    assert calls.count("sample_greedy") == 1


def test_context_sample_error_includes_native_error(tmp_path, monkeypatch):
    library, calls = _lifecycle_library(
        sample_result=13,
        last_error=b"Could not create the native greedy sampler.",
    )
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        with runtime.open_model(tmp_path / "model.gguf") as model:
            with model.open_context() as context:
                context.prefill([1, 2])
                with pytest.raises(NativeRuntimeError, match="greedy sampler"):
                    context.sample_greedy()
    assert calls[-1] == "runtime_destroy"


def test_context_prefill_second_attempt_is_rejected_by_native_contract(tmp_path, monkeypatch):
    library, calls = _lifecycle_library()
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        with runtime.open_model(tmp_path / "model.gguf") as model:
            with model.open_context() as context:
                assert context.prefill([1, 2]) == 2
                with pytest.raises(NativeRuntimeError, match="code 9"):
                    context.prefill([3])
    assert len([call for call in calls if isinstance(call, tuple) and call[0] == "prefill"]) == 2


def test_context_prefill_decode_failure_includes_native_error(tmp_path, monkeypatch):
    library, calls = _lifecycle_library(
        prefill_result=10,
        last_error=b"llama.cpp prompt prefill failed; create a fresh context before retrying.",
    )
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        with runtime.open_model(tmp_path / "model.gguf") as model:
            with model.open_context() as context:
                with pytest.raises(NativeRuntimeError, match="fresh context"):
                    context.prefill([1, 2, 3])
    assert calls[-1] == "runtime_destroy"


def test_runtime_closes_contexts_before_models_before_backend(tmp_path, monkeypatch):
    library, calls = _lifecycle_library(context_size=8192, batch_size=256)
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        model = runtime.open_model(tmp_path / "model.gguf", n_gpu_layers=7)
        first = model.open_context(n_ctx=8192, n_batch=256, n_threads=6)
        second = model.open_context()
    assert runtime.closed and model.closed and first.closed and second.closed
    assert calls[-4][0] == "context_destroy"
    assert calls[-3][0] == "context_destroy"
    assert calls[-2][0] == "model_close"
    assert calls[-1] == "runtime_destroy"


def test_context_close_is_idempotent(tmp_path, monkeypatch):
    library, calls = _lifecycle_library()
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        model = runtime.open_model(tmp_path / "model.gguf")
        context = model.open_context()
        context.close()
        context.close()
        model.close()
    assert len([call for call in calls if isinstance(call, tuple) and call[0] == "context_destroy"]) == 1
    assert len([call for call in calls if isinstance(call, tuple) and call[0] == "model_close"]) == 1


def test_context_create_error_includes_native_last_error(tmp_path, monkeypatch):
    library, calls = _lifecycle_library(
        context_result=5,
        last_error=b"llama.cpp could not create a context for the configured model.",
    )
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        with runtime.open_model(tmp_path / "model.gguf") as model:
            with pytest.raises(NativeRuntimeError, match="configured model"):
                model.open_context(n_ctx=4096)
    assert calls[-1] == "runtime_destroy"


def test_model_open_error_includes_native_last_error(tmp_path, monkeypatch):
    library, calls = _lifecycle_library(
        model_result=3,
        last_error=b"llama.cpp could not load the configured GGUF model.",
    )
    runtime_file = _install_fake_runtime(tmp_path, monkeypatch, library)
    with NativeRuntimeLoader(runtime_file).open_runtime() as runtime:
        with pytest.raises(NativeRuntimeError, match="configured GGUF model"):
            runtime.open_model(tmp_path / "broken.gguf")
    assert calls[-1] == "runtime_destroy"
