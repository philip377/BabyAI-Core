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


def test_ginger_uia_probe_inspects_deep_raw_and_control_trees():
    script = PROBE.read_text(encoding="utf-8")
    assert '[ValidateRange(1, 32)][int]$MaxDepth = 24' in script
    assert '[ValidateSet("Both", "Control", "Raw")]' in script
    assert 'TreeWalker]::RawViewWalker' in script
    assert 'TreeWalker]::ControlViewWalker' in script
    assert 'views = $scans.ToArray()' in script
    assert "max_depth_hit" in script



LISTVIEW_PROBE = (
    Path(__file__).resolve().parents[1]
    / "scripts" / "windows" / "ginger-listview-probe.ps1"
)


def test_ginger_listview_probe_looks_only_for_readable_window_controls():
    script = LISTVIEW_PROBE.read_text(encoding="utf-8")
    assert "GetSupportedPatterns()" in script
    assert "SysListView32" in script
    assert "LVM_GETITEMCOUNT" in script
    assert "SendMessageTimeoutW" in script
    assert "SMTO_ABORTIFHUNG" in script
    assert "listviews = $script:listviews.ToArray()" in script


def test_ginger_listview_probe_does_not_write_or_click():
    script = LISTVIEW_PROBE.read_text(encoding="utf-8").lower()
    assert "invoke-click" not in script
    assert "mouse_event" not in script
    assert "sendkeys" not in script
    assert "invoke-sqlcmd" not in script
    assert "lvm_setitem" not in script
    assert "postmessage" not in script



MSAA_PROBE = (
    Path(__file__).resolve().parents[1]
    / "scripts" / "windows" / "ginger-msaa-probe.ps1"
)


def test_ginger_msaa_probe_reads_accessible_children_only():
    code = MSAA_PROBE.read_text(encoding="utf-8")
    assert 'AccessibleObjectFromWindow' in code
    assert 'get_accName(id)' in code
    assert 'get_accRole(id)' in code
    assert 'accChildCount' in code
    assert 'SysListView32' in code
    assert '[switch]$IncludeNames' in code
    assert 'if (includeNames)' in code


def test_ginger_msaa_probe_has_bounds_and_no_unsafe_input():
    code = MSAA_PROBE.read_text(encoding="utf-8").lower()
    assert 'maxsamplerows = 12' in code
    assert 'maxvisited = 600' in code
    assert 'remote' not in code.replace('no remote memory allocation', '')
    assert 'writeprocessmemory' not in code
    assert 'sendinput' not in code
    assert 'sendkeys' not in code
    assert 'setforegroundwindow' not in code
    assert 'invoke-sqlcmd' not in code
    assert 'postmessage' not in code
