"""Exercise early script failures in isolation, without loading any UI driver."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import pytest

pytestmark = pytest.mark.skipif(os.name != "nt", reason="Windows PowerShell guard tests")


def invoke(tmp_path, *extra):
    script = tmp_path / "ginger-search-star.ps1"
    if not script.exists():
        shutil.copyfile(Path(__file__).resolve().parents[1] / "scripts/windows/ginger-search-star.ps1", script)
    shell = Path(os.environ["WINDIR"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"
    return subprocess.run([str(shell), "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                           "-File", str(script), "-Word", "Fixture", "-ReceiptDirectory", str(tmp_path / "receipt"),
                           *extra], capture_output=True, timeout=15)


def test_missing_driver_records_exact_preinput_phase(tmp_path):
    result = invoke(tmp_path, "-ConfirmNavigation", "-PreflightOnly")
    assert result.returncode != 0
    failure = json.loads((tmp_path / "receipt/failure.json").read_text(encoding="utf-8-sig"))
    assert failure["phase"] == "load_driver"
    assert not failure["filter_input_possible"] and not failure["automatic_retry_allowed"]
    assert not (tmp_path / "receipt/intent.json").exists()


def test_confirmation_required_before_any_artifact(tmp_path):
    assert invoke(tmp_path, "-PreflightOnly").returncode != 0
    assert not (tmp_path / "receipt").exists()


def test_existing_receipt_is_never_overwritten(tmp_path):
    folder = tmp_path / "receipt"; folder.mkdir()
    saved = folder / "failure.json"; saved.write_text("existing", encoding="utf-8")
    assert invoke(tmp_path, "-ConfirmNavigation").returncode != 0
    assert saved.read_text(encoding="utf-8") == "existing"
