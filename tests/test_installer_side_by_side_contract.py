from pathlib import Path


def test_installer_never_overwrites_an_existing_version_slot() -> None:
    root = Path(__file__).resolve().parents[1]
    program = (root / "installer" / "BabyAI.Setup" / "Program.cs").read_text(encoding="utf-8")
    allocator = (root / "installer" / "BabyAI.Setup" / "InstallSlotAllocator.cs").read_text(encoding="utf-8")

    assert "InstallSlotAllocator.Allocate(versionsRoot, manifest.Version)" in program
    assert "Directory.Move(tempDir, versionDir);" in program
    assert "Directory.Delete(versionDir, true)" not in program

    assert "var preferred = IOPath.Combine(versionsRoot, safeVersion);" in allocator
    assert "!Directory.Exists(preferred) && !File.Exists(preferred)" in allocator
    assert '$"{safeVersion}+{suffix}"' in allocator
    assert "Guid.NewGuid():N" in allocator
    assert "ValidateVersionSegment" in allocator


def test_install_slot_name_does_not_leak_into_display_version() -> None:
    root = Path(__file__).resolve().parents[1]
    program = (root / "installer" / "BabyAI.Setup" / "Program.cs").read_text(encoding="utf-8")
    shell = (root / "installer" / "BabyAI.Setup" / "ShellIntegration.cs").read_text(encoding="utf-8")
    uninstall = (root / "installer" / "BabyAI.Setup" / "UninstallIntegration.cs").read_text(encoding="utf-8")

    assert "ShellIntegration.CreateShortcuts(desktop, manifest.Version);" in program
    assert "CreateShortcuts(string desktopExe, string displayVersion)" in shell
    assert "RegisterFromDesktop(desktopExe, displayVersion);" in shell
    assert "RegisterFromDesktop(string desktopExe, string displayVersion)" in uninstall
    assert 'key.SetValue("DisplayVersion", version);' in uninstall
    assert "Register(installRoot, displayVersion.Trim(), desktopExe);" in uninstall
