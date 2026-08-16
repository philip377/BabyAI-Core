from pathlib import Path


def test_windows_release_bundles_and_selects_safe_cpu_runtime_tiers() -> None:
    root = Path(__file__).resolve().parents[1]
    cmake = (root / "native" / "BabyAI.NativeBridge" / "CMakeLists.txt").read_text(encoding="utf-8")
    workflow = (root / ".github" / "workflows" / "windows-release.yml").read_text(encoding="utf-8")
    bundle = (root / "scripts" / "windows" / "build-release-bundle.ps1").read_text(encoding="utf-8")
    verify = (root / "scripts" / "windows" / "verify-release-bundle.ps1").read_text(encoding="utf-8")
    bootstrap = (root / "desktop" / "BabyAI.Desktop" / "InstalledRuntimeBootstrap.cs").read_text(encoding="utf-8")
    commands = (root / "src" / "babyai" / "desktop_commands.py").read_text(encoding="utf-8")

    assert "portable avx avx2" in cmake
    assert 'STREQUAL "avx"' in cmake
    assert "set(GGML_AVX ON" in cmake
    assert "set(GGML_AVX2 OFF" in cmake
    assert "set(GGML_BMI2 OFF" in cmake

    assert "-DBABYAI_NATIVE_CPU_PROFILE=portable" in workflow
    assert "-DBABYAI_NATIVE_CPU_PROFILE=avx" in workflow
    assert "-DBABYAI_NATIVE_CPU_PROFILE=avx2" in workflow
    assert "-AvxRuntime" in workflow
    assert "-Avx2Runtime" in workflow

    assert '"runtime\\cpu-avx"' in bundle
    assert '"runtime\\cpu-avx2"' in bundle
    assert '"cpu-avx"' in bundle
    assert '"cpu-avx2"' in bundle

    assert '"runtime/cpu-avx/babyai_native.dll"' in verify
    assert '"runtime/cpu-avx2/babyai_native.dll"' in verify

    assert "Sse42.IsSupported" in bootstrap
    assert "Avx.IsSupported" in bootstrap
    assert "Avx2.IsSupported" in bootstrap
    assert "Bmi2.IsSupported" in bootstrap
    assert 'new CpuRuntimeSelection(avxRuntime, "avx")' in bootstrap
    assert 'new CpuRuntimeSelection(avx2Runtime, "avx2")' in bootstrap
    assert 'new CpuRuntimeSelection(portableRuntime, "portable")' in bootstrap
    assert 'Environment.SetEnvironmentVariable("BABYAI_NATIVE_CPU_PROFILE", cpu.Profile);' in bootstrap
    assert 'cpu_profile=os.getenv("BABYAI_NATIVE_CPU_PROFILE", "unknown")' in commands
