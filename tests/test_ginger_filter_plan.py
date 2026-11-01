import copy
import pytest
from test_ginger_search_transition import context
from babyai.ginger_filter_plan import FilterPlan
from babyai.ginger_price_queue import PriceQueueError


def initial():
    value = context()
    row = value["SearchWords"]["Candidates"][0]
    row.update(Title="Brand", Checked=False, StateImage=1)
    other = copy.deepcopy(row); other.update(Title="100", ObservedPosition=2)
    value["SearchWords"]["Candidates"].append(other)
    return value


def create(tmp_path):
    return FilterPlan.create(tmp_path / "plan", initial(), 7,
                             [{"word": "Brand", "enabled": True}, {"word": "100", "enabled": True}])


def test_two_steps_resume_without_replaying_input(tmp_path):
    journal = create(tmp_path); fresh = initial()
    for i in range(2):
        journal.begin(fresh, 7, confirmed=True)
        restarted = FilterPlan(journal.directory)
        assert restarted.status()["status"] == "observe_only"
        assert restarted.status()["next_step"] is None
        with pytest.raises(PriceQueueError): restarted.begin(fresh, 7, confirmed=True)
        fresh = copy.deepcopy(fresh)
        fresh["SearchWords"]["Candidates"][i].update(Checked=True, StateImage=2)
        result = restarted.observe(fresh, fresh, 7)
        assert result["completed_steps"] == i + 1
        journal = FilterPlan(journal.directory)
    assert journal.status()["status"] == "steps_observed"
    assert not journal.status()["search_completion_verified"]
    with pytest.raises(PriceQueueError): journal.begin(fresh, 7, confirmed=True)


@pytest.mark.parametrize("defect", ["confirmation", "process", "price", "word", "image"])
def test_begin_rejects_changed_context_before_event(tmp_path, defect):
    journal = create(tmp_path); fresh = initial(); pid = 7
    if defect == "process": pid = 8
    elif defect == "price":
        for panel in ("SearchWords", "Products"): fresh[panel]["Source"]["PriceText"] = "11"
    elif defect == "word": fresh["SearchWords"]["Candidates"][0]["Title"] = "Other"
    elif defect == "image": fresh["SearchWords"]["Candidates"][0]["StateImage"] = 2
    with pytest.raises(PriceQueueError): journal.begin(fresh, pid, confirmed=defect != "confirmation")
    assert not list(journal.directory.glob("event-*"))


@pytest.mark.parametrize("defect", ["unchanged", "other_filter", "candidate_change", "process", "panel"])
def test_unverified_observation_keeps_pending(tmp_path, defect):
    journal = create(tmp_path); fresh = initial(); journal.begin(fresh, 7, confirmed=True)
    fresh["SearchWords"]["Candidates"][0].update(Checked=True, StateImage=2)
    second = copy.deepcopy(fresh)
    if defect == "unchanged": fresh = second = initial()
    elif defect == "other_filter":
        for value in (fresh, second): value["SearchWords"]["Candidates"][1].update(Checked=True, StateImage=2)
    elif defect == "candidate_change": second["Products"]["Candidates"][0]["Title"] = "Later"
    elif defect == "panel":
        for value in (fresh, second): value["Products"]["PanelHandle"] += 1
    with pytest.raises(PriceQueueError): journal.observe(fresh, second, 8 if defect == "process" else 7)
    assert FilterPlan(journal.directory).status()["pending_step"] == 0


def test_partial_receipt_blocks_replay(tmp_path):
    journal = create(tmp_path)
    (journal.directory / "event-0000.json").write_text('{', encoding="utf-8")
    with pytest.raises(PriceQueueError): journal.status()


def test_missing_event_blocks_replay(tmp_path):
    journal = create(tmp_path); journal.begin(initial(), 7, confirmed=True)
    (journal.directory / "event-0000.json").rename(journal.directory / "event-0001.json")
    with pytest.raises(PriceQueueError): journal.status()


@pytest.mark.parametrize("steps", [[], [{"word":"Brand","enabled":1}],
    [{"word":"Absent","enabled":True}], [{"word":"Brand","enabled":True}]*2])
def test_bad_plan_not_created(tmp_path, steps):
    with pytest.raises(PriceQueueError): FilterPlan.create(tmp_path / "plan", initial(), 7, steps)
    assert not (tmp_path / "plan").exists()


def test_existing_plan_preserved(tmp_path):
    journal = create(tmp_path)
    original = (journal.directory / "plan.json").read_bytes()
    with pytest.raises(FileExistsError): create(tmp_path)
    assert (journal.directory / "plan.json").read_bytes() == original
