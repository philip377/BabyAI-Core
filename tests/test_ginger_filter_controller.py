import json
import os
from pathlib import Path
import subprocess
import pytest
from test_ginger_filter_plan import create, initial

pytestmark = pytest.mark.skipif(os.name != "nt", reason="Windows controller guards")


def run_controller(directory, operation, *extra):
    shell = Path(os.environ["WINDIR"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"
    script = Path(__file__).resolve().parents[1] / "scripts/windows/ginger-filter-step.ps1"
    return subprocess.run([str(shell), "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
        "-File", str(script), "-PlanDirectory", str(directory), "-Operation", operation, *extra],
        capture_output=True, timeout=20)


def test_no_confirmation_stops_before_lock(tmp_path):
    directory = tmp_path / "plan"
    assert run_controller(directory, "Step").returncode != 0
    assert not directory.with_name(directory.name + ".controller.lock").exists()


def test_pending_step_blocks_controller_before_ui(tmp_path):
    plan = create(tmp_path); plan.begin(initial(), 7, confirmed=True)
    result = run_controller(plan.directory, "Step", "-ConfirmNavigation")
    assert result.returncode != 0
    assert b"Pending or completed plan" in result.stderr
    assert len(list(plan.directory.glob("event-*"))) == 1


def test_observe_without_pending_stops_before_ui(tmp_path):
    plan = create(tmp_path)
    result = run_controller(plan.directory, "Observe")
    assert result.returncode != 0
    assert b"No pending filter step" in result.stderr


def test_status_reports_bound_pending_action(tmp_path):
    plan = create(tmp_path); plan.begin(initial(), 7, confirmed=True)
    result = run_controller(plan.directory, "Status")
    assert result.returncode == 0, result.stderr
    status = json.loads(result.stdout.decode("utf-8-sig"))
    assert status["pending_action"] == {"word": "Brand", "enabled": True}
    assert status["next_step"] is None


@pytest.mark.parametrize("steps", [[{"word":"MAN","enabled":True}],
    [{"word":"MAN","enabled":True},{"word":"TOO","enabled":False}]])
def test_windows_powershell_step_array_roundtrip(tmp_path, steps):
    source = (Path(__file__).resolve().parents[1] / "scripts/windows/ginger-filter-step.ps1").read_text(encoding="utf-8-sig")
    start = source.index("$rawSteps=")
    end = source.index("\n}\n$source=", start)
    fixture = tmp_path / "parse.ps1"
    fixture.write_text("$StepsFile=Join-Path $PSScriptRoot 'steps.json'\n" + source[start:end] +
                       "\n[pscustomobject]@{steps=$steps} | ConvertTo-Json -Depth 5", encoding="utf-8-sig")
    (tmp_path / "steps.json").write_text(json.dumps(steps), encoding="utf-8")
    shell = Path(os.environ["WINDIR"]) / "System32/WindowsPowerShell/v1.0/powershell.exe"
    result = subprocess.run([str(shell), "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(fixture)],
                            capture_output=True, timeout=15)
    assert result.returncode == 0, result.stderr
    assert json.loads(result.stdout)["steps"] == steps
