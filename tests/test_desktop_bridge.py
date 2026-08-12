from __future__ import annotations

from babyai.config import BabyAIConfig
from babyai.desktop_bridge import build_desktop_snapshot
from babyai.permissions import Capability, PermissionStore
from babyai.working_memory import TaskState, WorkingMemoryStore


def test_desktop_bridge_has_stable_schema_and_is_read_only(tmp_path):
    config = BabyAIConfig(data_dir=tmp_path, owner="tester", name="BabyAI", provider="echo")
    WorkingMemoryStore(config.working_memory_file).save(
        TaskState(goal="Build the desktop orb", status="active", context="Windows")
    )
    PermissionStore(config.permissions_file).grant(Capability.SYSTEM_INFO)

    before = sorted(path.name for path in tmp_path.iterdir())
    snapshot = build_desktop_snapshot(config).as_dict()
    after = sorted(path.name for path in tmp_path.iterdir())

    assert snapshot["schema_version"] == 1
    assert snapshot["identity"]["name"] == "BabyAI"
    assert snapshot["task"]["goal"] == "Build the desktop orb"
    assert snapshot["permissions"]["system.info"] is True
    assert snapshot["permissions"]["filesystem.read"] is False
    assert "next_step" in snapshot["learning"]
    assert before == after


def test_desktop_bridge_empty_state_is_valid(tmp_path):
    config = BabyAIConfig(data_dir=tmp_path, owner="tester", provider="echo")
    snapshot = build_desktop_snapshot(config).as_dict()

    assert snapshot["schema_version"] == 1
    assert snapshot["task"] is None
    assert snapshot["learning"]["hypothesis"] is None
    assert snapshot["learning"]["evidence_count"] == 0
    assert snapshot["learning"]["curiosity"] is None
    assert snapshot["learning"]["lesson"] is None
