from __future__ import annotations

from pathlib import Path


def test_native_shim_exposes_stable_abi_v4_prefill_contract():
    root = Path(__file__).resolve().parents[1]
    header = (root / "native" / "BabyAI.NativeBridge" / "include" / "babyai_native.h").read_text(
        encoding="utf-8"
    )
    source = (root / "native" / "BabyAI.NativeBridge" / "src" / "babyai_native.cpp").read_text(
        encoding="utf-8"
    )

    assert "#define BABYAI_NATIVE_ABI_VERSION 4u" in header
    for symbol in (
        "babyai_native_abi_version",
        "babyai_native_runtime_create",
        "babyai_native_runtime_destroy",
        "babyai_native_model_open",
        "babyai_native_model_close",
        "babyai_native_model_tokenize",
        "babyai_native_context_create",
        "babyai_native_context_destroy",
        "babyai_native_context_n_ctx",
        "babyai_native_context_n_batch",
        "babyai_native_context_prefill",
        "babyai_native_context_token_count",
        "babyai_native_last_error",
    ):
        assert symbol in header
        assert symbol in source

    assert "llama_backend_init();" in source
    assert "llama_backend_free();" in source
    assert "llama_model_load_from_file" in source
    assert "llama_model_get_vocab" in source
    assert "llama_tokenize" in source
    assert "llama_context_default_params" in source
    assert "llama_init_from_model" in source
    assert "llama_n_ctx" in source
    assert "llama_n_batch" in source
    assert "llama_decode" in source
    assert "std::vector<llama_token> native_tokens" in source
    assert "llama_batch batch" in source

    # Prefill owns its temporary batch storage through C++ RAII rather than the
    # pinned llama.cpp raw-malloc batch helper, and sampling remains out of scope.
    assert "llama_batch_init(" not in source
    assert "llama_batch_get_one(" not in source
    assert "llama_get_logits" not in source
    assert "llama_sampler_sample" not in source


def test_native_shim_ci_pins_upstream_and_runs_managed_lifecycle_smoke():
    root = Path(__file__).resolve().parents[1]
    workflow = (root / ".github" / "workflows" / "native-shim.yml").read_text(encoding="utf-8")
    cmake = (root / "native" / "BabyAI.NativeBridge" / "CMakeLists.txt").read_text(encoding="utf-8")
    smoke = (root / "scripts" / "native_shim_smoke.py").read_text(encoding="utf-8")

    assert "e79e4bf660e19f2ad851e06c6913f7a8c5852621" in workflow
    assert "BUILD_SHARED_LIBS OFF" in cmake
    assert "target_link_libraries(babyai_native PRIVATE llama)" in cmake
    assert "NativeRuntimeLoader" in smoke
    assert ".open_runtime()" in smoke
    assert "runtime.open_model(" in smoke
    assert "definitely-missing-babyai-model.gguf" in smoke
