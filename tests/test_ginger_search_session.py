import copy
import pytest
from test_ginger_search_transition import context
from babyai.ginger_search_session import reconcile_search
from babyai.ginger_price_queue import PriceQueueError


def example():
    first = context()
    first["SearchWords"]["Candidates"][0].update(Checked=True, StateImage=2)
    second = copy.deepcopy(first)
    before = copy.deepcopy(first["SearchWords"])
    before["Candidates"][0].update(Checked=None, StateImage=0)
    intent = {"word": "Same text", "enabled": True, "before": before, "process_id": 7, "can_write": False}
    return intent, first, second


def test_pending_intent_can_be_observed_without_claiming_completion():
    result = reconcile_search(*example(), 7)
    assert result["filter_state_verified"] and result["observations_agree"]
    assert not result["automatic_retry_allowed"] and not result["search_completion_verified"]
    assert not result["review"]["can_match"]


@pytest.mark.parametrize("defect", ["process", "source", "word", "state", "results", "image", "other", "permission"])
def test_recovery_fails_closed(defect):
    intent, first, second = example()
    if defect == "process": intent["process_id"] = 8
    elif defect == "source": intent["before"]["Source"]["PriceText"] = "11"
    elif defect == "word": intent["word"] = "Unknown"
    elif defect == "state": intent["enabled"] = False
    elif defect == "results": second["Products"]["Candidates"][0]["Title"] = "Later result"
    elif defect == "image": first["SearchWords"]["Candidates"][0]["StateImage"] = 0
    elif defect == "permission": intent["can_write"] = True
    else:
        for panel in (intent["before"], first["SearchWords"], second["SearchWords"]):
            row = copy.deepcopy(panel["Candidates"][0]); row.update(Title="Other", ObservedPosition=2)
            panel["Candidates"].append(row)
        intent["before"]["Candidates"][1].update(Checked=False, StateImage=1)
    with pytest.raises(PriceQueueError): reconcile_search(intent, first, second, 7)


def test_disabled_filter_and_empty_candidates_still_need_review():
    intent, first, second = example()
    intent["enabled"] = False
    for value in (first, second):
        value["SearchWords"]["Candidates"][0].update(StateImage=0, Checked=None)
        value["Products"]["Candidates"] = []
    result = reconcile_search(intent, first, second, 7)
    assert not result["review"]["absence_proven"]
