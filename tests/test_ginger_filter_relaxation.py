import copy
import pytest
from test_ginger_row_filter_prepare import fixture
from babyai.ginger_filter_relaxation import propose_removal
from babyai.ginger_price_queue import PriceQueueError


def setup(tmp_path):
    journal, report, ctx = fixture(tmp_path)
    for row in ctx["SearchWords"]["Candidates"]:
        row.update(Checked=True, StateImage=2)
    source = ctx["SearchWords"]["Source"]
    keys = ("TableHandle", "ObservedRowPosition", "DocumentNumber", "Supplier",
            "Title", "PriceText", "LinkedProductText")
    return journal, report, ctx, {k: source[k] for k in keys}


def test_explicit_removal_preserves_other_filters_and_requires_confirmation(tmp_path):
    journal, report, ctx, source = setup(tmp_path)
    before = copy.deepcopy(ctx)
    result = propose_removal(journal.directory, report, ctx, expected_source=source, words=["MAN"])
    assert result["steps"] == [{"word": "MAN", "enabled": False}]
    assert result["confirmation_required"] and not result["can_match"]
    assert ctx == before


@pytest.mark.parametrize("defect", ["supplier", "title", "price", "process", "pending", "missing", "duplicate", "unknown", "words"])
def test_stale_ambiguous_or_unknown_removal_blocked(tmp_path, defect):
    journal, report, ctx, source = setup(tmp_path)
    words = ["MAN"]
    if defect == "supplier": source["Supplier"] = "Other"
    elif defect == "title": source["Title"] = "Other"
    elif defect == "price": source["PriceText"] = "999"
    elif defect == "process": report["process_id"] += 1
    elif defect == "pending": journal.begin(report)
    elif defect == "missing": words = ["absent"]
    elif defect == "duplicate": words *= 2
    elif defect == "unknown": ctx["SearchWords"]["Candidates"][1].update(Checked=None, StateImage=0)
    else: ctx["SearchWords"]["Candidates"][0]["Title"] = "Other"
    with pytest.raises(PriceQueueError):
        propose_removal(journal.directory, report, ctx, expected_source=source, words=words)


def test_already_clear_filter_needs_no_toggle(tmp_path):
    journal, report, ctx, source = setup(tmp_path)
    ctx["SearchWords"]["Candidates"][1].update(Checked=False, StateImage=1)
    result = propose_removal(journal.directory, report, ctx, expected_source=source, words=["MAN"])
    assert result["steps"] == [] and result["already_clear"] == ["MAN"]


def test_cli_prepares_controller_plan_and_rejects_reuse(tmp_path):
    import json
    import subprocess
    import sys
    from pathlib import Path
    from babyai.ginger_filter_plan import FilterPlan
    journal, report, ctx, source = setup(tmp_path)
    observed = tmp_path / "observation.json"
    request = tmp_path / "request.json"
    output = tmp_path / "prepared"
    observed.write_text(json.dumps({"rows_report": report, "context": ctx}), encoding="utf-8")
    request.write_text(json.dumps({"source": source, "words": ["MAN"]}), encoding="utf-8")
    script = Path(__file__).resolve().parents[1] / "scripts/windows/ginger-filter-removal.py"
    command = [sys.executable, str(script), "--row-directory", str(journal.directory),
               "--report", str(observed), "--request", str(request), "--output-directory", str(output)]
    result = subprocess.run(command, cwd=tmp_path, capture_output=True, timeout=15)
    assert result.returncode == 0, result.stderr
    payload = json.loads(result.stdout)
    assert payload["input_sent"] is False
    plan = FilterPlan(payload["plan_directory"])
    assert plan.status()["next_step"] == {"word": "MAN", "enabled": False}
    with pytest.raises(PriceQueueError): plan.begin(ctx, 7)
    changed = copy.deepcopy(ctx)
    changed["SearchWords"]["Candidates"][0].update(Checked=False, StateImage=1)
    with pytest.raises(PriceQueueError): plan.begin(changed, 7, confirmed=True)
    plan.begin(ctx, 7, confirmed=True)
    after = copy.deepcopy(ctx)
    after["SearchWords"]["Candidates"][1].update(Checked=False, StateImage=1)
    plan.observe(after, after, 7)
    assert plan.status()["status"] == "steps_observed"
    repeated = subprocess.run(command, cwd=tmp_path, capture_output=True, timeout=15)
    assert repeated.returncode != 0
    assert plan.status()["status"] == "steps_observed"


def test_invalid_request_creates_no_output(tmp_path):
    from babyai.ginger_filter_relaxation import prepare_removal
    journal, report, ctx, source = setup(tmp_path)
    source["Title"] = "Other"
    output = tmp_path / "prepared"
    with pytest.raises(PriceQueueError):
        prepare_removal(journal.directory, {"rows_report": report, "context": ctx},
                        {"source": source, "words": ["MAN"]}, output)
    assert not output.exists()
