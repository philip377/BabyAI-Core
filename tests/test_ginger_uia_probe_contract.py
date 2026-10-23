from pathlib import Path


PROBE = (
    Path(__file__).resolve().parents[1]
    / "scripts" / "windows" / "ginger-uia-probe.ps1"
)


def test_ginger_uia_probe_avoids_powershell_list_array_binder_bug():
    script = PROBE.read_text(encoding="utf-8")
    assert "[System.Collections.Generic.List[object]]::new()" in script
    assert "elements = $script:rows.ToArray()" in script
    assert "elements = @($script:rows)" not in script


def test_ginger_uia_probe_remains_read_only():
    script = PROBE.read_text(encoding="utf-8").lower()
    assert "uiautomationclient" in script
    assert "getfirstchild" in script
    assert "getnextsibling" in script
    assert "set-value" not in script
    assert "invoke-click" not in script
    assert "sendkeys" not in script
    assert "mouse_event" not in script
    assert "invoke-sqlcmd" not in script
