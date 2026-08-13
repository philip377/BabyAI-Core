from __future__ import annotations

from pathlib import Path


def test_vulkan_native_workflow_is_pinned_and_separate_from_cpu_default():
    root = Path(__file__).resolve().parents[1]
    workflow = (root / ".github" / "workflows" / "native-vulkan-shim.yml").read_text(encoding="utf-8")
    cpu_workflow = (root / ".github" / "workflows" / "native-shim.yml").read_text(encoding="utf-8")

    assert 'VULKAN_SDK_VERSION: "1.4.350.0"' in workflow
    assert 'VULKAN_SDK_SHA256: "855b27ba05d2d8119c5114c5d4ff870ca38f2c632b11e1bb9923b9b7e6ecfe7b"' in workflow
    assert "copy_only=1" in workflow
    assert "Get-FileHash -Algorithm SHA256" in workflow
    assert "-DBABYAI_NATIVE_BACKEND=vulkan" in workflow
    assert "inspect_native_backend" in workflow
    assert "info.build_backend == 'vulkan'" in workflow
    assert "BabyAI-Native-Shim-Vulkan-x64" in workflow

    # Existing release path stays correctness-first CPU unless explicitly changed.
    assert "-DBABYAI_NATIVE_BACKEND=vulkan" not in cpu_workflow
