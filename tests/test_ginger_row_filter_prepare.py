import copy
import pytest
from test_ginger_search_transition import context
from babyai.ginger_price_queue import PriceQueueError
from babyai.ginger_row_journal import RowNavigationJournal
from babyai.ginger_filter_plan import FilterPlan
from babyai.ginger_row_filter_prepare import propose_filters, prepare_row_filters


def fixture(tmp_path):
    ctx = context()
    title = "MARBERT MAN TOO ash/l 100 ml"
    for panel in ("SearchWords", "Products"): ctx[panel]["Source"]["Title"] = title
    original = ctx["SearchWords"]["Candidates"][0]
    ctx["SearchWords"]["Candidates"] = []
    for index, word in enumerate(("MARBERT", "MAN", "TOO", "ash", "/", "l", "100", "ml"), 1):
        row = dict(original, Title=word, ObservedPosition=index, Checked=None, StateImage=0)
        ctx["SearchWords"]["Candidates"].append(row)
    source = ctx["SearchWords"]["Source"]
    report = {"process_id":7, "can_write":False,
              "rows":[{"Selected":True, "Observation":copy.deepcopy(source)},
                      {"Selected":False,"Observation":dict(source, ObservedRowPosition=2, Title="Other")}]}
    journal = RowNavigationJournal(tmp_path / "rows"); journal.create(report)
    return journal, report, ctx


def test_plan_bound_to_journalled_row_and_only_adds_words(tmp_path):
    journal, report, ctx = fixture(tmp_path)
    result = prepare_row_filters(journal.directory, report, ctx, tmp_path/"filters", tmp_path/"proposal.json")
    assert {step["word"] for step in result["steps"]} == {"MARBERT", "MAN", "TOO"}
    assert all(step["enabled"] is True for step in result["steps"])
    assert result["ambiguous_words_preserved"] == ["ash", "/", "l"]
    assert FilterPlan(tmp_path/"filters").status()["total_steps"] == 3
    assert journal.status()["selected_sampled"] is False
    assert not result["absence_proven"]


def test_existing_filters_preserved_and_no_empty_plan(tmp_path):
    journal, report, ctx = fixture(tmp_path)
    for row in ctx["SearchWords"]["Candidates"]:
        if row["Title"] in ("MARBERT", "MAN", "TOO", "100", "l"):
            row.update(Checked=True, StateImage=2)
    result = prepare_row_filters(journal.directory, report, ctx, tmp_path/"filters", tmp_path/"proposal.json")
    assert result["steps"] == []
    assert result["status"] == "filters_already_set_review_results"
    assert not (tmp_path/"filters").exists()


@pytest.mark.parametrize("defect", ["pending", "process", "price", "other_row", "stale_words", "permission"])
def test_unsafe_preparation_rejected_before_artifacts(tmp_path, defect):
    journal, report, ctx = fixture(tmp_path)
    if defect == "pending": journal.begin(report)
    elif defect == "process": report["process_id"] = 8
    elif defect == "price": report["rows"][0]["Observation"]["PriceText"] = "11"
    elif defect == "other_row":
        for panel in ("SearchWords", "Products"): ctx[panel]["Source"]["ObservedRowPosition"] = 2
    elif defect == "stale_words": ctx["SearchWords"]["Candidates"][0]["Title"] = "Other"
    else: ctx["CanMatch"] = True
    with pytest.raises(PriceQueueError):
        prepare_row_filters(journal.directory, report, ctx, tmp_path/"filters", tmp_path/"proposal.json")
    assert not (tmp_path/"proposal.json").exists()
    assert not (tmp_path/"filters").exists()


def test_no_mutation_of_observed_context(tmp_path):
    _, _, ctx = fixture(tmp_path); original = copy.deepcopy(ctx)
    propose_filters(ctx)
    assert ctx == original


def test_existing_proposal_not_replaced(tmp_path):
    journal, report, ctx = fixture(tmp_path)
    path=tmp_path/"proposal.json"; path.write_text("keep")
    with pytest.raises(FileExistsError): prepare_row_filters(journal.directory, report, ctx, tmp_path/"filters", path)
    assert path.read_text() == "keep"


def test_adjacent_duplicate_supplier_word_collapsed_by_ginger(tmp_path):
    _, _, ctx=fixture(tmp_path)
    for panel in ("SearchWords","Products"): ctx[panel]["Source"]["Title"]="HUGO BOSS BOSS SOUL MAN edt 90 ml"
    template=ctx["SearchWords"]["Candidates"][0]
    ctx["SearchWords"]["Candidates"]=[dict(template,Title=word,ObservedPosition=i) for i,word in enumerate("HUGO BOSS SOUL MAN edt 90 ml".split(),1)]
    assert propose_filters(ctx)["steps"]
    ctx["SearchWords"]["Candidates"][2]["Title"]="OTHER"
    with pytest.raises(PriceQueueError): propose_filters(ctx)


@pytest.mark.parametrize("source,words,allowed", [
    ("CHANEL CRISTALLE edt 100ml tester", "CHANEL CRISTALLE edt 100 ml tester", True),
    ("CHANEL CRISTALLE edt 100ml tester", "CHANEL CRISTALLE edt 10 ml tester", False),
    ("CARTIER DELICES DE CARTIER parfum 30 ml vintage", "CARTIER DELICES DE parfum 30 ml vintage", True),
    ("CARTIER DELICES DE CARTIER parfum 30 ml vintage", "CARTIER DE parfum 30 ml vintage", False),
    ("CARTIER DELICES DE CARTIER parfum 30 ml vintage", "DELICES CARTIER DE parfum 30 ml vintage", False),
    ("A 30 ml B 30 ml", "A 30 ml B", False),
    ("A / B / A", "A / B", False),
])
def test_nonadjacent_duplicate_preserves_variants_order_numbers_punctuation(tmp_path, source, words, allowed):
    _, _, ctx=fixture(tmp_path)
    for panel in ("SearchWords","Products"): ctx[panel]["Source"]["Title"]=source
    template=ctx["SearchWords"]["Candidates"][0]
    ctx["SearchWords"]["Candidates"]=[dict(template,Title=word,ObservedPosition=i) for i,word in enumerate(words.split(),1)]
    if allowed: assert propose_filters(ctx)["can_match"] is False
    else:
        with pytest.raises(PriceQueueError): propose_filters(ctx)
