import copy
import pytest
from test_ginger_row_filter_prepare import fixture
from babyai.ginger_filter_plan import FilterPlan, _save
from babyai.ginger_row_filter_prepare import prepare_row_filters
from babyai.ginger_row_review import finish_row_review
from babyai.ginger_price_queue import PriceQueueError


def setup(tmp_path, *, no_actions=True):
    journal, rows, ctx = fixture(tmp_path)
    if no_actions:
        for row in ctx["SearchWords"]["Candidates"]:
            if row["Title"] in ("MARBERT","MAN","TOO"): row.update(Checked=True, StateImage=2)
    folder = tmp_path/"preparation"; folder.mkdir()
    _save(folder/"prepare-filters.json", {"rows_report":rows,"context":ctx})
    prepare_row_filters(journal.directory, rows, ctx, folder/"filter-plan", folder/"proposal.json")
    return journal, folder, {"rows_report":rows,"first":copy.deepcopy(ctx),"second":copy.deepcopy(ctx)}


def test_no_action_review_recorded_and_idempotent(tmp_path):
    journal, folder, report = setup(tmp_path)
    result = finish_row_review(journal.directory, folder, report)
    assert result["row_status"]["selected_sampled"]
    assert result["status"] == "needs_manual_review"
    assert not result["automatic_next_row"] and not result["products_processed"]
    assert finish_row_review(journal.directory, folder, report) == result
    assert len(list(journal.directory.glob('[0-9]*.json'))) == 1


def test_complete_filter_plan_required(tmp_path):
    journal, folder, report = setup(tmp_path, no_actions=False)
    with pytest.raises(PriceQueueError): finish_row_review(journal.directory, folder, report)
    plan = FilterPlan(folder/"filter-plan")
    current = report["first"]
    while plan.status()["next_step"] is not None:
        word = plan.status()["next_step"]["word"]
        plan.begin(current, 7, confirmed=True)
        current = copy.deepcopy(current)
        for row in current["SearchWords"]["Candidates"]:
            if row["Title"] == word: row.update(Checked=True, StateImage=2)
        plan.observe(current, current, 7)
    report.update(first=current, second=copy.deepcopy(current))
    assert finish_row_review(journal.directory, folder, report)["row_status"]["selected_sampled"]


@pytest.mark.parametrize("defect", ["pending_row", "price", "process", "candidate_change", "changed_filter", "wrong_source"])
def test_stale_review_rejected_before_sampling(tmp_path, defect):
    journal, folder, report = setup(tmp_path)
    if defect == "pending_row": journal.begin(report["rows_report"])
    elif defect == "price": report["rows_report"]["rows"][0]["Observation"]["PriceText"]="11"
    elif defect == "process": report["rows_report"]["process_id"]=8
    elif defect == "candidate_change": report["second"]["Products"]["Candidates"][0]["Title"]="Later"
    elif defect == "changed_filter":
        for name in ("first","second"): report[name]["SearchWords"]["Candidates"][0].update(Checked=None, StateImage=0)
    else:
        for name in ("first","second"):
            for panel in ("SearchWords","Products"): report[name][panel]["Source"]["Title"]="Other"
    with pytest.raises(PriceQueueError): finish_row_review(journal.directory, folder, report)
    assert not journal.status()["selected_sampled"]
    assert not (folder/"row-review.json").exists()


def test_resume_after_report_written_before_sample(tmp_path, monkeypatch):
    journal, folder, report = setup(tmp_path)
    from babyai.ginger_row_journal import RowNavigationJournal
    original = RowNavigationJournal.record_sample
    def fail(*args): raise OSError("simulated interruption")
    monkeypatch.setattr(RowNavigationJournal,"record_sample",fail)
    with pytest.raises(OSError): finish_row_review(journal.directory, folder, report)
    assert (folder/"row-review.json").exists()
    assert not journal.status()["selected_sampled"]
    monkeypatch.setattr(RowNavigationJournal,"record_sample",original)
    assert finish_row_review(journal.directory, folder, report)["row_status"]["selected_sampled"]


def test_empty_result_never_proves_absence(tmp_path):
    journal, folder, report = setup(tmp_path)
    for name in ("first","second"): report[name]["Products"]["Candidates"] = []
    result = finish_row_review(journal.directory, folder, report)
    import json
    saved=json.loads((folder/"row-review.json").read_text(encoding="utf-8"))
    assert not saved["review"]["absence_proven"]
    assert result["status"] == "needs_manual_review"
