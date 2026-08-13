from __future__ import annotations

from pathlib import Path


def test_native_shim_exposes_stable_abi_v1_contract():
    root = Path(__file__).resolve().parents[1]
    header = (root / "native" / "BabyAI.NativeBridge" / "include" / "babyai_native.h").read_text(
        encoding="utf-8"
    )
    source = (root / "native" / "BabyAI.NativeBridge" / "src" / "babyai_native.cpp").read_text(
        encoding="utf-8"
    )

    assert "#define BABYAI_NATIVE_ABI_VERSION 1u" in header
    for symbol in (
        "babyai_native_abi_version",
        "babyai_native_runtime_create",
        "babyai_native_runtime_destroy",
        "babyai_native_model_open",
        "babyai_native_model_close",
        "babyai_native_last_error",
    ):
        assert symbol in header
        assert symbol in source

    assert "llama_backend_init();" in source
    assert "llama_backend_free();" in source
    assert "llama_model_load_from_file" in source
    assert "llama_model_free" in source
    assert "llama_init_from_model" not in source
    assert "llama_decode" not in source


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
