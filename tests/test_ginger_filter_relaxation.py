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
