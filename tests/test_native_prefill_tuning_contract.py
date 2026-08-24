from pathlib import Path


def test_native_prefill_uses_bounded_physical_batch():
    source = Path("native/BabyAI.NativeBridge/src/babyai_native.cpp").read_text(encoding="utf-8")

    assert "k_prefill_ubatch_cap = 1024" in source
    assert "params.n_ubatch = std::min(params.n_batch, k_prefill_ubatch_cap);" in source
    assert "params.n_ubatch > params.n_batch" not in source
