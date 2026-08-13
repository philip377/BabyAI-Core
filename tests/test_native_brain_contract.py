from __future__ import annotations

from pathlib import Path

from babyai.config import BabyAIConfig


def test_native_model_defaults_inside_babyai_data_dir(tmp_path):
    config = BabyAIConfig(data_dir=tmp_path, provider="native")

    assert config.native_model_file == tmp_path / "models" / "babyai.gguf"


def test_native_runtime_defaults_inside_babyai_data_dir(tmp_path):
    config = BabyAIConfig(data_dir=tmp_path, provider="native")

    assert config.native_runtime_file.parent == tmp_path / "runtime"
    assert config.native_runtime_file.name in {
        "babyai_native.dll",
        "libbabyai_native.so",
        "libbabyai_native.dylib",
    }


def test_native_paths_can_come_from_environment(tmp_path, monkeypatch):
    model = tmp_path / "custom.gguf"
    runtime = tmp_path / "custom-babyai-native.dll"
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path / "state"))
    monkeypatch.setenv("BABYAI_PROVIDER", "native")
    monkeypatch.setenv("BABYAI_NATIVE_MODEL", str(model))
    monkeypatch.setenv("BABYAI_NATIVE_RUNTIME", str(runtime))

    config = BabyAIConfig.default()

    assert config.provider == "native"
    assert config.native_model_file == model
    assert config.native_runtime_file == runtime


def test_windows_ui_knows_native_readiness_states():
    root = Path(__file__).resolve().parents[1]
    behavior = (root / "desktop" / "BabyAI.Desktop" / "BrainStatusBehavior.cs").read_text(encoding="utf-8")
    startup = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.BrainStatus.cs").read_text(encoding="utf-8")

    assert '"native_model_missing"' in behavior
    assert '"native_runtime_missing"' in behavior
    assert '"native_inference_pending"' in behavior
    assert "BABYAI_NATIVE_MODEL" in behavior
    assert "BABYAI_NATIVE_RUNTIME" in behavior
    assert 'brain.Provider.Equals("native"' in startup
    assert '"native_model_missing"' in startup
    assert '"native_runtime_missing"' in startup
    assert '"native_inference_pending"' in startup
