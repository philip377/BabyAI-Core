import copy
import json
from pathlib import Path
import subprocess
import sys

import pytest

from babyai.ginger_filter_only import propose_filter_only, read_filter_only_json
from babyai.ginger_filter_plan import _query
from babyai.ginger_price_queue import PriceQueueError
from babyai.ginger_row_filter_prepare import propose_filters
from babyai.ginger_search_transition import _context


def context():
    words = "ATELIER LUNA BOIS NOIR edp 75 ml tester с крышкой".split()
    source = {"TableHandle": 100, "ObservedRowPosition": 12, "DocumentNumber": "doc-17",
              "Supplier": "Synthetic supplier", "Title": " ".join(words), "PriceText": "10",
              "LinkedProductText": "<none>", "IdentityVerified": False, "CanMatch": False}
    return {"SearchWords": {
        "Source": source, "PanelHandle": 200, "IdentityVerified": False, "CanMatch": False,
        "SearchCompletionVerified": False,
        "Candidates": [{"ObservedPosition": index, "Title": word, "DescriptionText": "",
                        "Selected": False, "Checked": word == "LUNA", "StateImage": 2 if word == "LUNA" else 1,
                        "IdentityVerified": False, "DefaultAction": None, "Bounds": [0, index * 10, 80, 10]}
                       for index, word in enumerate(words, 1)]},
        "ProductPanelHandle": 201, "ProductRows": 278, "QueryWordsMatchSource": True,
        "CanMatch": False, "SearchCompletionVerified": False}


def legacy_context():
    value = context()
    products = copy.deepcopy(value["SearchWords"])
    products["PanelHandle"] = value["ProductPanelHandle"]
    products["Candidates"] = [dict(products["Candidates"][0], Title="Observed catalogue product")]
    return {"SearchWords": value["SearchWords"], "Products": products,
            "CanMatch": False, "SearchCompletionVerified": False}


def test_overflow_278_adds_only_unique_observed_unchecked_words_without_claiming_absence():
    value = context()
    original = copy.deepcopy(value)
    result = propose_filter_only(value)
    assert result["steps"]
    assert {step["word"] for step in result["steps"]} == {"ATELIER", "BOIS", "NOIR"}
    unchecked = {row["Title"] for row in value["SearchWords"]["Candidates"] if row["Checked"] is not True}
    assert all(step["word"] in unchecked and step["enabled"] is True for step in result["steps"])
    assert result["verification_only_words"] == ["с", "крышкой"]
    assert result["existing_filters_preserved"] is True
    assert all(result[field] is False for field in
               ("can_write", "can_match", "absence_proven", "search_completion_verified", "catalogue_frequency_available"))
    assert "Products" not in value and "Candidates" not in result
    assert value == original
    assert result == propose_filters(legacy_context())


def test_existing_filters_are_preserved_and_all_set_is_not_an_empty_catalogue():
    value = context()
    for row in value["SearchWords"]["Candidates"]:
        row.update(Checked=True, StateImage=2)
    result = propose_filter_only(value)
    assert result["steps"] == []
    assert result["status"] == "filters_already_set_review_results"
    assert result["absence_proven"] is False
    assert result["preserved_verification_filters"] == ["с", "крышкой"]


def test_new_path_never_fabricates_a_legacy_product_snapshot(monkeypatch):
    def forbidden(_):
        raise AssertionError("The filter-only path called the legacy context validator")

    monkeypatch.setattr("babyai.ginger_row_filter_prepare._query", forbidden)
    assert propose_filter_only(context())["steps"]


def test_context_modes_are_not_interchangeable():
    for validator in (_context, _query, propose_filters):
        with pytest.raises(PriceQueueError):
            validator(context())
    with pytest.raises(PriceQueueError):
        propose_filter_only(legacy_context())


@pytest.mark.parametrize("value", [None, [], "context", 1, True])
def test_non_object_context_rejected(value):
    with pytest.raises(PriceQueueError):
        propose_filter_only(value)


@pytest.mark.parametrize("count", [None, False, True, "278", 278.0, -1, 0, 1, 256])
def test_product_count_must_be_integer_overflow(count):
    value = context()
    value["ProductRows"] = count
    with pytest.raises(PriceQueueError):
        propose_filter_only(value)


@pytest.mark.parametrize("path,bad", [
    (("ProductPanelHandle",), True), (("ProductPanelHandle",), 0), (("ProductPanelHandle",), 200),
    (("ProductPanelHandle",), "201"), (("QueryWordsMatchSource",), False), (("QueryWordsMatchSource",), 1),
    (("CanMatch",), True), (("CanMatch",), 0), (("SearchCompletionVerified",), True),
    (("SearchCompletionVerified",), None), (("SearchWords",), []),
    (("SearchWords", "PanelHandle"), True), (("SearchWords", "PanelHandle"), -1),
    (("SearchWords", "CanMatch"), True), (("SearchWords", "IdentityVerified"), True),
    (("SearchWords", "SearchCompletionVerified"), True), (("SearchWords", "Source"), []),
    (("SearchWords", "Source", "CanMatch"), True), (("SearchWords", "Source", "IdentityVerified"), True),
    (("SearchWords", "Source", "TableHandle"), False), (("SearchWords", "Source", "TableHandle"), 0),
    (("SearchWords", "Source", "ObservedRowPosition"), True),
    (("SearchWords", "Source", "ObservedRowPosition"), 0),
    (("SearchWords", "Source", "ObservedRowPosition"), 501),
    (("SearchWords", "Source", "Supplier"), ""), (("SearchWords", "Source", "DocumentNumber"), None),
    (("SearchWords", "Source", "PriceText"), 10), (("SearchWords", "Source", "LinkedProductText"), " "),
    (("SearchWords", "Source", "Title"), "Another row"),
    (("SearchWords", "Source", "Title"), "x" * 513),
    (("SearchWords", "Candidates"), []), (("SearchWords", "Candidates"), {}),
])
def test_invalid_source_flags_handles_and_partial_search_are_rejected(path, bad):
    value = context()
    target = value
    for key in path[:-1]:
        target = target[key]
    target[path[-1]] = bad
    with pytest.raises(PriceQueueError):
        propose_filter_only(value)


@pytest.mark.parametrize("level", ["context", "panel", "source", "row"])
def test_each_required_field_is_required_and_extra_claims_are_rejected(level):
    def part(value):
        return {"context": value, "panel": value["SearchWords"], "source": value["SearchWords"]["Source"],
                "row": value["SearchWords"]["Candidates"][0]}[level]

    for field in set(part(context())) - {"DefaultAction", "Bounds"}:
        value = context()
        del part(value)[field]
        with pytest.raises(PriceQueueError):
            propose_filter_only(value)
    value = context()
    part(value)["CanWrite"] = True
    with pytest.raises(PriceQueueError):
        propose_filter_only(value)


@pytest.mark.parametrize("products", [None, {}, {"Candidates": []}])
def test_even_an_empty_products_field_is_forbidden(products):
    value = context()
    value["Products"] = products
    with pytest.raises(PriceQueueError):
        propose_filter_only(value)


@pytest.mark.parametrize("field,bad", [
    ("ObservedPosition", True), ("ObservedPosition", 2), ("Title", ""), ("Title", "Absent"),
    ("Title", "x" * 513), ("DescriptionText", None), ("DescriptionText", "x" * 2049),
    ("Selected", 0), ("Checked", 1), ("Checked", True), ("StateImage", True), ("StateImage", 3),
    ("StateImage", 2), ("IdentityVerified", True), ("DefaultAction", []),
    ("Bounds", None), ("Bounds", [0, 0, 1]), ("Bounds", [False, 0, 1, 1]), ("Bounds", [0, 0, -1, 1]),
])
def test_invalid_word_rows_and_checkbox_states_are_rejected(field, bad):
    value = context()
    value["SearchWords"]["Candidates"][0][field] = bad
    with pytest.raises(PriceQueueError):
        propose_filter_only(value)


@pytest.mark.parametrize("defect", ["order", "missing", "duplicate", "count", "non_object"])
def test_incomplete_reordered_duplicated_and_oversized_word_panels_rejected(defect):
    value = context()
    rows = value["SearchWords"]["Candidates"]
    if defect == "order":
        rows[0]["Title"], rows[1]["Title"] = rows[1]["Title"], rows[0]["Title"]
    elif defect == "missing":
        rows.pop()
    elif defect == "duplicate":
        rows.append(dict(rows[0], ObservedPosition=len(rows) + 1))
        value["SearchWords"]["Source"]["Title"] += " " + rows[0]["Title"]
    elif defect == "count":
        value["SearchWords"]["Candidates"] = [dict(rows[0], ObservedPosition=i) for i in range(1, 258)]
    else:
        rows[0] = None
    with pytest.raises(PriceQueueError):
        propose_filter_only(value)


def test_collapsed_supplier_name_preserves_existing_lexical_validation():
    value = context()
    value["SearchWords"]["Source"]["Title"] = value["SearchWords"]["Source"]["Title"].replace("LUNA", "LUNA LUNA")
    assert propose_filter_only(value)["steps"]


@pytest.mark.parametrize("raw", ['{"ProductRows":278,"ProductRows":279}',
    '{"Source":{"Title":"first","Title":"other"}}', '{"x":NaN}', '{"x":Infinity}',
    '{"x":-Infinity}', '{broken}', '{} trailing', '[' * 1100 + ']' * 1100],
    ids=["duplicate-count", "duplicate-source", "nan", "infinity", "negative-infinity", "broken", "trailing", "deep-array"])
def test_new_mode_rejects_duplicate_keys_nonfinite_and_malformed_json(raw):
    with pytest.raises(PriceQueueError):
        read_filter_only_json(raw)


def test_cli_explicit_dispatch_and_legacy_regression(tmp_path):
    script = Path(__file__).resolve().parents[1] / "scripts/windows/ginger-desktop-plan.py"
    overflow = tmp_path / "filter-only.json"
    overflow.write_text(json.dumps(context(), ensure_ascii=False), encoding="utf-8-sig")
    legacy = tmp_path / "legacy.json"
    legacy.write_text(json.dumps(legacy_context()), encoding="utf-8")

    def run(path, *options):
        return subprocess.run([sys.executable, str(script), str(path), *options],
                              capture_output=True, text=True, check=False, timeout=20)

    result = run(overflow, "--filter-only")
    assert result.returncode == 0, result.stderr
    assert json.loads(result.stdout) == propose_filter_only(context())
    result = run(legacy)
    assert result.returncode == 0, result.stderr
    assert json.loads(result.stdout) == propose_filters(legacy_context())
    assert run(overflow).returncode != 0
    assert run(legacy, "--filter-only").returncode != 0
    overflow.write_text('{"ProductRows":278,"ProductRows":279}', encoding="utf-8")
    result = run(overflow, "--filter-only")
    assert result.returncode != 0 and not result.stdout.strip()
