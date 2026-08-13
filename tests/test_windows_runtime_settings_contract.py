from __future__ import annotations

from pathlib import Path


def test_desktop_runtime_choice_is_persistent_and_process_local():
    root = Path(__file__).resolve().parents[1]
    store = (root / "desktop" / "BabyAI.Desktop" / "DesktopSettingsStore.cs").read_text(encoding="utf-8")
    settings = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.Settings.cs").read_text(encoding="utf-8")

    assert 'string NativeAcceleration = "cpu"' in store
    assert '"BABYAI_NATIVE_ACCELERATION"' in settings
    assert "EnvironmentVariableTarget.Process" in settings
    assert '"auto" => 0' in settings
    assert '"vulkan" => 1' in settings
    assert '"CPU"' in settings
    assert "settings with { NativeAcceleration = mode }" in settings
    assert "settings with { AlwaysOnTop = alwaysOnTop.IsOn }" in settings
    assert '"BABYAI_NATIVE_VULKAN_RUNTIME"' in settings
