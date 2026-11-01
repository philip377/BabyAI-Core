from copy import deepcopy
import pytest
from babyai.ginger_candidate_review import review_snapshot
from babyai.ginger_supplier_matcher import CatalogPosition, SupplierPosition, explain_candidate


def snapshot():
    return {"CanMatch": False, "IdentityVerified": False, "SearchCompletionVerified": False,
            "Source": {"CanMatch": False, "IdentityVerified": False, "Supplier": "fixture",
                       "DocumentNumber": "1", "Title": "MARBERT MAN TOO ash/l 100 ml",
                       "PriceText": "20", "LinkedProductText": "none", "ObservedRowPosition": 3},
            "Candidates": [{"ObservedPosition": 1, "Title": "Marbert HOMME тв 100ml (тестер)",
                            "IdentityVerified": False}]}


def test_observed_case_explains_name_and_unknown_attributes():
    result = review_snapshot(snapshot())
    item = result["candidates"][0]
    assert "name" in {v["field"] for v in item["differences"]}
    assert {"tester", "concentration", "supplier_abbreviation"} <= {v["field"] for v in item["uncertainties"]}
    assert not item["can_match"] and not result["can_write"]
    assert "sku" not in item


def test_same_title_does_not_prove_identity_or_freshness():
    value = snapshot()
    value["Candidates"][0]["Title"] = value["Source"]["Title"]
    item = review_snapshot(value)["candidates"][0]
    assert not item["differences"]
    assert not item["identity_verified"] and not item["search_completion_verified"]
    assert item["status"] == "manual_review"


def test_empty_results_do_not_prove_absence():
    value = snapshot(); value["Candidates"] = []
    result = review_snapshot(value)
    assert result["candidates"] == [] and not result["absence_proven"]


@pytest.mark.parametrize("field", ["CanMatch", "IdentityVerified", "SearchCompletionVerified"])
def test_snapshot_claims_rejected(field):
    value = snapshot(); value[field] = True
    with pytest.raises(ValueError): review_snapshot(value)


@pytest.mark.parametrize("mutation", [
    lambda v: v["Source"].update(CanMatch=True),
    lambda v: v["Source"].update(Title=""),
    lambda v: v["Source"].update(ObservedRowPosition=True),
    lambda v: v["Candidates"][0].update(ObservedPosition=True),
    lambda v: v["Candidates"][0].update(IdentityVerified=True),
    lambda v: v.update(Candidates=v["Candidates"] * 257),
    lambda v: v["Candidates"][0].update(Title="x" * 513),
])
def test_invalid_observations_rejected(mutation):
    value = snapshot(); mutation(value)
    with pytest.raises(ValueError): review_snapshot(value)


def test_explicit_tester_difference_and_volume_difference():
    item = explain_candidate(SupplierPosition("s", "A EDP 50 ml", is_tester=False),
                             CatalogPosition("c", "A EDT 100 ml", is_tester=True))
    assert {"volume", "tester", "concentration"} <= {x["field"] for x in item["differences"]}
    assert not item["can_match"]


def test_review_does_not_mutate_snapshot():
    value = snapshot(); before = deepcopy(value)
    review_snapshot(value)
    assert value == before


def test_cli_uses_checkout_and_preserves_existing_output(tmp_path):
    import json
    import subprocess
    import sys
    from pathlib import Path
    root = Path(__file__).resolve().parents[1]
    incoming = tmp_path / "snapshot.json"
    outgoing = tmp_path / "review.json"
    incoming.write_text(json.dumps({"candidate_snapshot": snapshot()}), encoding="utf-8-sig")
    command = [sys.executable, str(root / "scripts/windows/ginger-candidate-review.py"),
               str(incoming), "--output", str(outgoing)]
    first = subprocess.run(command, capture_output=True, cwd=tmp_path)
    assert first.returncode == 0, first.stderr
    original = outgoing.read_bytes()
    assert json.loads(original)["candidates"][0]["uncertainties"][2]["candidate"] is True
    assert subprocess.run(command, capture_output=True, cwd=tmp_path).returncode != 0
    assert outgoing.read_bytes() == original
