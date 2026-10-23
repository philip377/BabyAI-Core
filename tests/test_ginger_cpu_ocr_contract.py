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
    assert 'Get-Process -Name "Ginger4"' in code
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



SETUP = (
    Path(__file__).resolve().parents[1] / "scripts" / "windows"
    / "setup-ginger-russian-ocr.ps1"
)


def test_user_scope_russian_data_installer():
    code = SETUP.read_text(encoding="utf-8")
    assert "tesseract-ocr/tessdata_fast/main/rus.traineddata" in code
    assert "Join-Path $env:LOCALAPPDATA" in code
    assert "eng.traineddata" in code
    assert "rus.traineddata" in code
    assert "--list-langs" in code
    assert "Invoke-WebRequest" in code
    assert "Ginger4" not in code
    assert "Start-Process" not in code
    assert "Program Files" not in code or "Copy-Item" in code


def test_cpu_ocr_detects_russian_training_data_and_upscales():
    code = SCRIPT.read_text(encoding="utf-8")
    assert "BabyAI\\Tesseract\\tessdata" in code
    assert "2x for the small" in code
    assert "language_data_directory=$dataDirLabel" in code
    assert "image_upscale_factor=2" in code
    assert '@ocrArgs' in code
    assert "--tessdata-dir" in code



def test_cpu_ocr_can_inspect_upper_supplier_grid_without_guessing_selected_row():
    code = SCRIPT.read_text(encoding="utf-8")
    assert '[switch]$CaptureSupplierGrid' in code
    assert 'if($CaptureSupplierGrid)' in code
    assert 'supplier-grid.png' in code
    assert 'supplier_grid_lines=$supplierGridLines' in code
    assert 'supplier_grid_ocr_requested=[bool]$CaptureSupplierGrid' in code
    assert 'selected_supplier_row_verified=$false' in code
    assert 'safe_for_automatic_matching=$false' in code
    assert "OCR results are unverified" in code
    assert 'Run-Ocr $supplierGridImage $supplierGridBase $lang 11' in code
    assert 'Ginger changed during supplier-grid capture' in code
    assert 'NOT a verified selected product row' in code



def test_upper_grid_tsv_preserves_word_coordinates_for_visual_row_review():
    script = SCRIPT.read_text(encoding="utf-8")
    assert '$gridTop=$start.Y+[int]($start.H*0.16)' in script
    assert 'function Read-Grid-OcrSegments' in script
    assert 'if($customTessdata){$argsList+=@("--tessdata-dir",$customTessdata)}' in script
    assert '$argsList+="tsv"' in script
    assert 'Import-Csv -LiteralPath $tsvFile -Delimiter ' in script
    assert 'supplier_grid_segments=$supplierGridSegments' in script
    assert 'supplier_grid_segment_coordinates="relative_to_2x_supplier_grid_png"' in script
    assert 'selected_supplier_row_verified=$false' in script
    assert 'safe_for_automatic_matching=$false' in script
