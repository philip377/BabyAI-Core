from __future__ import annotations

from types import SimpleNamespace

import pytest

from babyai.agent import AgentExecutor, ToolCall
from babyai.permissions import Capability, PermissionStore
from babyai.runtime_trace import process_memory_metrics, trace


def test_all_read_only_tools_execute_behind_their_capabilities(tmp_path, monkeypatch) -> None:
    folder = tmp_path / "folder"
    folder.mkdir()
    note = folder / "note.txt"
    note.write_text("hello", encoding="utf-8")
    permissions = PermissionStore(tmp_path / "permissions.json")
    executor = AgentExecutor(permissions)
    monkeypatch.setattr("babyai.tools._is_windows", lambda: True)
    monkeypatch.setattr("babyai.tools._windows_process_list", lambda: ["42: BabyAI.exe"])

    calls = (
        (Capability.SYSTEM_INFO, ToolCall("system.info", {})),
        (Capability.PROCESS_LIST, ToolCall("process.list", {})),
        (Capability.FILESYSTEM_LIST, ToolCall("filesystem.list", {"path": str(folder)})),
        (Capability.FILESYSTEM_READ, ToolCall("filesystem.read", {"path": str(note)})),
    )
    for capability, call in calls:
        with pytest.raises(PermissionError):
            executor.execute(call)
        permissions.grant(capability)
        result = executor.execute(call)
        assert result
        permissions.revoke(capability)


def test_one_shot_permission_is_never_persisted_and_revokes_on_error(tmp_path) -> None:
    permissions_file = tmp_path / "permissions.json"
    permissions = PermissionStore(permissions_file)
    executor = AgentExecutor(permissions)
    note = tmp_path / "note.txt"
    note.write_text("one shot", encoding="utf-8")

    assert executor.execute_once(ToolCall("filesystem.read", {"path": str(note)})) == "one shot"
    assert not permissions_file.exists()
    assert not permissions.is_granted(Capability.FILESYSTEM_READ)

    with pytest.raises(FileNotFoundError):
        executor.execute_once(ToolCall("filesystem.read", {"path": str(tmp_path / "missing.txt")}))
    assert not permissions_file.exists()
    assert not permissions.is_granted(Capability.FILESYSTEM_READ)


def test_one_shot_execution_preserves_an_existing_persistent_grant(tmp_path) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    permissions.grant(Capability.FILESYSTEM_READ)
    note = tmp_path / "note.txt"
    note.write_text("persistent", encoding="utf-8")

    result = AgentExecutor(permissions).execute_once(
        ToolCall("filesystem.read", {"path": str(note)})
    )

    assert result == "persistent"
    assert permissions.is_granted(Capability.FILESYSTEM_READ)


def test_windows_process_listing_uses_a_fixed_bounded_command(tmp_path, monkeypatch) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    permissions.grant(Capability.PROCESS_LIST)
    captured: dict[str, object] = {}

    def fake_run(command, **kwargs):
        captured["command"] = command
        captured.update(kwargs)
        return SimpleNamespace(stdout=b'"BabyAI.Desktop.exe","123","Console","1","12,000 K"\r\n')

    monkeypatch.setattr("babyai.tools._is_windows", lambda: True)
    monkeypatch.setattr("babyai.tools.subprocess.run", fake_run)

    result = AgentExecutor(permissions).execute(ToolCall("process.list", {}))

    assert result == '[\n  "123: BabyAI.Desktop.exe"\n]'
    assert captured["command"] == ["tasklist.exe", "/fo", "csv", "/nh"]
    assert captured["timeout"] == 5
    assert captured["check"] is True


def test_runtime_trace_records_available_process_memory(tmp_path, monkeypatch) -> None:
    log = tmp_path / "runtime.log"
    monkeypatch.setenv("BABYAI_RUNTIME_LOG", str(log))
    metrics = process_memory_metrics()

    trace("baseline.sample", **metrics)

    line = log.read_text(encoding="utf-8")
    assert "baseline.sample" in line
    if metrics:
        assert metrics["working_set_mb"] >= 0
        assert metrics["private_mb"] >= 0
        assert "working_set_mb=" in line
        assert "private_mb=" in line
