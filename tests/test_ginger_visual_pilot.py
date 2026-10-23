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
    result = interpret_ginger_frame(_frame(candidates=("BRUNO BANANI INTENSE (муж) EDP 30ML",)))
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
    result = interpret_ginger_frame(_frame("ambiguous", candidates=("BRUNO BANANI INTENSE EDP 30ML",)))
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



def test_desktop_bridge_command_returns_observation_without_linking(tmp_path, monkeypatch):
    from babyai.config import BabyAIConfig
    from babyai.desktop_commands import DesktopCommands
    from babyai.ginger_visual_pilot import GingerPilotObservation

    def fake_observe(store, port):
        assert port == 8077
        assert store.directory == tmp_path / "screen_captures"
        return GingerPilotObservation(
            capture_id="a" * 32,
            supplier_title="BRUNO BANANI MAN INTENSE",
            words=("BRUNO", "INTENSE"),
            candidates=("BRUNO BANANI",),
            state="review_existing",
            next_step="verify_exact_catalogue_identity",
            warnings=(),
        )

    monkeypatch.setattr("babyai.desktop_commands.observe_ginger_once", fake_observe)
    config = BabyAIConfig(data_dir=tmp_path, provider="echo")
    result = DesktopCommands(config).execute("ginger.observe", {"port": 8077})
    assert result["ok"] is True
    assert result["ginger"]["supplier_title"] == "BRUNO BANANI MAN INTENSE"
    assert result["ginger"]["read_only"] is True
    assert result["ginger"]["ginger_updated"] is False


def test_desktop_bridge_command_fails_closed_when_ginger_is_not_foreground(tmp_path, monkeypatch):
    from babyai.config import BabyAIConfig
    from babyai.desktop_commands import DesktopCommands, DesktopCommandError

    def refused(store, port):
        raise GingerPilotError("Activate Ginger4.exe before inspection")

    monkeypatch.setattr("babyai.desktop_commands.observe_ginger_once", refused)
    with pytest.raises(DesktopCommandError, match="Activate Ginger4.exe"):
        DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider="echo")).execute(
            "ginger.observe"
        )


def test_local_vision_start_script_is_loopback_and_manual_only():
    from pathlib import Path
    root = Path(__file__).resolve().parents[1]
    script = (root / "scripts" / "windows" / "start-ginger-vision.ps1").read_text(
        encoding="utf-8"
    )
    assert '"127.0.0.1"' in script
    assert '"local-vision"' in script
    assert "ggml-org/Qwen2.5-VL-3B-Instruct-GGUF:Q4_K_M" in script
    assert "Invoke-WebRequest" not in script
    assert "Invoke-RestMethod" not in script
    assert "Start-Process" not in script
    assert "Ginger4" not in script


def test_intense_source_cannot_match_base_even_if_vlm_says_possible_match():
    result = interpret_ginger_frame(_frame())
    assert result.state == "template_discovery_only"
    assert result.next_step == "find_exact_flanker_or_propose_new_sku"
    assert "intense" in " ".join(result.warnings)
    assert result.as_dict()["ginger_updated"] is False


def test_qahwa_source_cannot_match_plain_khamrah():
    result = interpret_ginger_frame(
        _frame(
            title="LATTAFA KHAMRAH QAHWA EDP 100ML",
            candidates=("LATTAFA KHAMRAH EDP 100ML",),
        )
    )
    assert result.state == "template_discovery_only"
    assert result.read_only



def test_unix_agent_exposes_ginger_observation_only_with_screen_permission(tmp_path):
    from babyai.agent import AgentExecutor, ToolCall
    from babyai.permissions import Capability, PermissionStore

    permissions = PermissionStore(tmp_path / "permissions.json")
    agent = AgentExecutor(permissions)
    assert "ginger.observe" in agent.tool_names()
    assert "READ-ONLY" in agent.catalog()
    assert agent.required_capability(ToolCall("ginger.observe", {})) == Capability.SCREEN_CAPTURE
    assert agent.tool_compatible_with_intent("посмотри ginger", "ginger.observe")
    assert not agent.tool_compatible_with_intent("расскажи про парфюмерию", "ginger.observe")
    assert not agent.is_allowed(ToolCall("ginger.observe", {}))


def test_agent_ginger_observation_rejects_unknown_arguments(tmp_path):
    from babyai.agent import AgentExecutor, ToolCall, ToolProtocolError
    from babyai.permissions import PermissionStore

    agent = AgentExecutor(PermissionStore(tmp_path / "permissions.json"))
    with pytest.raises(ToolProtocolError, match="Unexpected|unsupported"):
        agent.execute(ToolCall("ginger.observe", {"unsafe_click": [20, 30]}))
