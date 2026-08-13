from __future__ import annotations

from pathlib import Path


def test_desktop_runtime_choice_is_persistent_and_bridge_scoped():
    root = Path(__file__).resolve().parents[1]
    desktop = root / "desktop" / "BabyAI.Desktop"
    store = (desktop / "DesktopSettingsStore.cs").read_text(encoding="utf-8")
    settings = (desktop / "MainWindow.Settings.cs").read_text(encoding="utf-8")
    bridge = (desktop / "BabyAIBridgeClient.cs").read_text(encoding="utf-8")

    assert 'string NativeAcceleration = "cpu"' in store
    assert '"auto" => 0' in settings
    assert '"vulkan" => 1' in settings
    assert '"CPU"' in settings
    assert "settings with { NativeAcceleration = mode }" in settings
    assert "settings with { AlwaysOnTop = alwaysOnTop.IsOn }" in settings
    assert '"BABYAI_NATIVE_VULKAN_RUNTIME"' in settings
    assert "ApplySavedNativeAcceleration(startInfo)" in bridge
    assert "DesktopUiSettingsStore().Load().NativeAcceleration" in bridge
    assert "Process.Start(startInfo)" in bridge
