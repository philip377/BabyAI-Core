from __future__ import annotations

from dataclasses import dataclass
import pytest

from babyai.ginger_local_vision import GingerVisualObservation
from babyai.ginger_visual_pilot import (
    GingerPilotError, interpret_ginger_frame, observe_ginger_once,
    require_ginger_foreground,
)


def _frame(status="possible_match", title="BRUNO BANANI MAN INTENSE EDP 30ML", candidates=("BRUNO BANANI (муж) EDP 30ML",)):
    return GingerVisualObservation(
        observation_id="a" * 32,
        selected_supplier=title,
        starred_words=("BRUNO", "BANANI", "INTENSE"),
        visible_catalog_candidates=candidates,
        status=status,
        warnings=(),
    )


def test_existing_candidate_requires_exact_identity_not_auto_link():
    result = interpret_ginger_frame(_frame())
    assert result.state == "review_existing"
    assert result.next_step == "verify_exact_catalogue_identity"
    assert result.read_only
    assert result.as_dict()["ginger_updated"] is False


def test_no_candidates_researches_without_creating_any_product():
    result = interpret_ginger_frame(_frame("no_visible_candidates", candidates=()))
    assert result.state == "research_missing"
    assert result.next_step == "check_aliases_and_perfumery_sources"
    assert result.as_dict()["ginger_updated"] is False


def test_unreadable_unselected_or_contradictory_frame_pauses():
    assert interpret_ginger_frame(_frame("unreadable")).state == "pause"
    assert interpret_ginger_frame(_frame("possible_match", title=None)).state == "pause"
    assert interpret_ginger_frame(_frame("no_visible_candidates", candidates=("A",))).state == "pause"


def test_ambiguous_flanker_is_never_linked():
    result = interpret_ginger_frame(_frame("ambiguous"))
    assert result.state == "review_ambiguity"
    assert result.next_step == "do_not_link_similar_variants"


@dataclass
class FakeCapture:
    id: str = "a" * 32
    mode: str = "active_window"


class FakeStore:
    def __init__(self):
        self.deleted = []
        self.captures = []
    def capture(self, mode):
        self.captures.append(mode)
        return FakeCapture()
    def delete(self, observation_id):
        self.deleted.append(observation_id)


def test_one_shot_reads_only_from_same_ginger_foreground():
    store = FakeStore()
    ports = []
    class FakeVision:
        def __init__(self, local_store, *, port):
            assert local_store is store
            ports.append(port)
        def analyze(self, observation_id):
            assert observation_id == "a" * 32
            return _frame()
    result = observe_ginger_once(
        store, port=8081, foreground=lambda: 1234, analyzer_factory=FakeVision,
    )
    assert result.read_only
    assert result.capture_id == "a" * 32
    assert ports == [8081]
    assert store.captures == ["active_window"]
    assert store.deleted == []


def test_foreground_changed_during_capture_discards_it_without_vision():
    handles = iter([10, 20])
    class MustNotAnalyze:
        def __init__(self, *args, **kwargs):
            raise AssertionError("Must never analyze a screenshot from other window")
    store = FakeStore()
    with pytest.raises(GingerPilotError, match="window changed"):
        observe_ginger_once(
            store, foreground=lambda: next(handles),
            analyzer_factory=MustNotAnalyze,
        )
    assert store.deleted == ["a" * 32]


def test_ginger_pilot_unavailable_on_other_operating_system():
    import os
    if os.name != "nt":
        with pytest.raises(GingerPilotError, match="Windows"):
            require_ginger_foreground()


def test_no_implicit_screen_grant_in_pilot_source():
    from inspect import getsource
    source = getsource(observe_ginger_once)
    assert ".capture(" in source
    assert "grant(" not in source
    assert "click" not in source
    assert "send_input" not in source
