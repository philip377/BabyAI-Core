from pathlib import Path


def test_desktop_performance_settings_select_and_persist_native_acceleration() -> None:
    root = Path(__file__).resolve().parents[1]
    settings = (
        root / "desktop" / "BabyAI.Desktop" / "MainWindow.Settings.cs"
    ).read_text(encoding="utf-8")
    bootstrap = (
        root / "desktop" / "BabyAI.Desktop" / "InstalledRuntimeBootstrap.cs"
    ).read_text(encoding="utf-8")
    bridge = (
        root / "desktop" / "BabyAI.Desktop" / "BabyAIBridgeClient.cs"
    ).read_text(encoding="utf-8")

    assert '("cpu", "Процессор (CPU)")' in settings
    assert '("vulkan", "Видеокарта (GPU · Vulkan)")' in settings
    assert '("hybrid", "GPU + CPU · сбалансированный")' in settings
    assert "InstalledRuntimeBootstrap.SaveAccelerationPreference(mode)" in settings
    assert "_bridge.RestartWorker()" in settings

    assert '"hybrid"' in bootstrap
    assert 'Environment.SetEnvironmentVariable("BABYAI_NATIVE_ACCELERATION", acceleration)' in bootstrap
    assert "File.Move(temporaryPath, path, overwrite: true)" in bootstrap
    assert "public void RestartWorker()" in bridge


def test_runtime_label_exposes_selected_native_acceleration() -> None:
    root = Path(__file__).resolve().parents[1]
    window = (
        root / "desktop" / "BabyAI.Desktop" / "MainWindow.xaml.cs"
    ).read_text(encoding="utf-8")

    assert '"cpu" => "CPU"' in window
    assert '"vulkan" => "GPU"' in window
    assert '"hybrid" => "GPU + CPU"' in window
    assert 'return $"Runtime: native · {accelerationLabel} · {modelName}";' in window
