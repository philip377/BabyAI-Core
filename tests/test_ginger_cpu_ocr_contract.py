from pathlib import Path


SCRIPT = (
    Path(__file__).resolve().parents[1] / "scripts" / "windows"
    / "test-ginger-cpu-ocr.ps1"
)


def test_cpu_ocr_pilot_runs_on_windows_without_model_or_gpu():
    code = SCRIPT.read_text(encoding="utf-8")
    assert "TesseractExe" in code
    assert 'Get-Command tesseract.exe' in code
    assert 'GetForegroundWindow' in code
    assert 'GetWindowThreadProcessId' in code
    assert 'SysListView32' in code
    assert 'EnumChildWindows' in code
    assert 'Graphics]::FromImage' in code
    assert '"eng+rus"' in code
    assert 'ImageFormat]::Png' in code
    assert 'ConvertTo-Json' in code
    assert 'read_only=$true' in code
    assert 'ginger_modified=$false' in code
    assert 'recommendation="review_only_no_linking"' in code


def test_cpu_ocr_pilot_never_automates_erp_or_calls_cloud():
    code = SCRIPT.read_text(encoding="utf-8").lower()
    for token in (
        "sendinput", "mouse_event", "sendkeys", "setcursorpos",
        "postmessage", "setwindowtext", "invoke-sqlcmd",
        "invoke-restmethod", "invoke-webrequest", "https://",
        "writeprocessmemory", "createprocess", "start-process",
    ):
        assert token not in code, token


def test_cpu_ocr_pilot_does_not_use_captured_text_as_commands():
    code = SCRIPT.read_text(encoding="utf-8")
    assert 'No buttons clicked' not in code or 'ginger_modified=$false' in code
    assert 'if($owner.ProcessName -ine "Ginger4")' in code
    assert 'ActiveHandle() -ne $start.Hwnd' in code
    assert 'Remove-Item -LiteralPath $leftPng,$rightPng' in code
    assert '"intense","qahwa","elixir","extreme","absolu"' in code



def test_cpu_ocr_auto_activates_ginger_when_launched_from_terminal():
    code = SCRIPT.read_text(encoding="utf-8")
    assert 'Get-Process -Name "Ginger4"' in code
    assert 'SetForegroundWindow' in code
    assert 'ShowWindow' in code
    assert '::Activate($gingerHwnd)' in code
    assert '::ActivePid() -ne [uint32]$ginger.Id' in code
    assert 'Start-Sleep -Seconds 4' in code
    assert 'Ginger4 did not become active' in code
    assert 'More than one Ginger4 main window' in code
