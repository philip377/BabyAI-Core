from __future__ import annotations

import json
import urllib.error

from babyai.config import BabyAIConfig
from babyai.desktop_bridge import build_desktop_snapshot
from babyai.permissions import Capability, PermissionStore
from babyai.working_memory import TaskState, WorkingMemoryStore


class _Response:
    def __init__(self, payload: dict[str, object]) -> None:
        self._payload = json.dumps(payload).encode("utf-8")

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        return None

    def read(self) -> bytes:
        return self._payload


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
    assert snapshot["runtime"] == {
        "provider": "echo",
        "model": "qwen3:8b",
        "state": "ready",
        "ready": True,
        "detail": "Echo diagnostics provider is ready.",
    }
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
    assert snapshot["runtime"]["ready"] is True


def test_desktop_bridge_reports_ready_ollama_model(tmp_path, monkeypatch):
    monkeypatch.setattr(
        "babyai.desktop_bridge.urllib.request.urlopen",
        lambda request, timeout: _Response({"models": [{"name": "qwen3:8b"}]}),
    )
    config = BabyAIConfig(data_dir=tmp_path, provider="ollama", model="qwen3:8b")

    runtime = build_desktop_snapshot(config).as_dict()["runtime"]

    assert runtime["state"] == "ready"
    assert runtime["ready"] is True


def test_desktop_bridge_reports_missing_ollama_model(tmp_path, monkeypatch):
    monkeypatch.setattr(
        "babyai.desktop_bridge.urllib.request.urlopen",
        lambda request, timeout: _Response({"models": [{"name": "llama3.2:3b"}]}),
    )
    config = BabyAIConfig(data_dir=tmp_path, provider="ollama", model="qwen3:8b")

    runtime = build_desktop_snapshot(config).as_dict()["runtime"]

    assert runtime["state"] == "model_missing"
    assert runtime["ready"] is False
    assert "qwen3:8b" in runtime["detail"]


def test_desktop_bridge_reports_unreachable_ollama(tmp_path, monkeypatch):
    def _offline(request, timeout):
        raise urllib.error.URLError("connection refused")

    monkeypatch.setattr("babyai.desktop_bridge.urllib.request.urlopen", _offline)
    config = BabyAIConfig(data_dir=tmp_path, provider="ollama", model="qwen3:8b")

    runtime = build_desktop_snapshot(config).as_dict()["runtime"]

    assert runtime["state"] == "unavailable"
    assert runtime["ready"] is False
