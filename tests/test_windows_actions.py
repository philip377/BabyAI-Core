from __future__ import annotations

import json

import pytest

from babyai.agent import AgentExecutor, ToolCall, ToolProtocolError
from babyai.identity import Identity
from babyai.llm import EchoProvider
from babyai.memory import SQLiteMemoryStore
from babyai.permissions import Capability, PermissionStore
from babyai.primus import Primus
from babyai.tool_approval import PendingToolApprovalStore


def test_file_write_is_bounded_explicit_and_one_shot(tmp_path) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    executor = AgentExecutor(permissions)
    target = tmp_path / "created.txt"
    call = ToolCall(
        "filesystem.write",
        {"path": str(target), "content": "hello", "overwrite": False},
    )

    with pytest.raises(PermissionError):
        executor.execute(call)
    result = executor.execute_once(call)

    assert "Wrote 5 bytes" in result
    assert target.read_text(encoding="utf-8") == "hello"
    assert not permissions.is_granted(Capability.FILESYSTEM_WRITE)

    with pytest.raises(FileExistsError):
        executor.execute_once(call)
    assert target.read_text(encoding="utf-8") == "hello"
    assert not permissions.is_granted(Capability.FILESYSTEM_WRITE)


def test_file_overwrite_requires_an_explicit_boolean(tmp_path) -> None:
    target = tmp_path / "existing.txt"
    target.write_text("before", encoding="utf-8")
    permissions = PermissionStore(tmp_path / "permissions.json")
    permissions.grant(Capability.FILESYSTEM_WRITE)
    executor = AgentExecutor(permissions)

    with pytest.raises(ToolProtocolError, match="true or false"):
        executor.execute(
            ToolCall(
                "filesystem.write",
                {"path": str(target), "content": "after", "overwrite": "yes"},
            )
        )
    assert target.read_text(encoding="utf-8") == "before"

    executor.execute(
        ToolCall(
            "filesystem.write",
            {"path": str(target), "content": "after", "overwrite": True},
        )
    )
    assert target.read_text(encoding="utf-8") == "after"


def test_application_and_diagnostic_commands_use_fixed_allowlists(tmp_path, monkeypatch) -> None:
    launched: list[tuple[str, ...]] = []
    run: list[tuple[str, ...]] = []
    monkeypatch.setattr("babyai.windows_actions._launch_fixed", launched.append)
    monkeypatch.setattr(
        "babyai.windows_actions._run_fixed",
        lambda command: run.append(command) or "tester\\owner",
    )
    permissions = PermissionStore(tmp_path / "permissions.json")
    executor = AgentExecutor(permissions)

    assert executor.execute_once(ToolCall("application.open", {"name": "notepad"})) == "Opened notepad."
    assert executor.execute_once(ToolCall("command.run", {"command": "whoami"})) == "tester\\owner"
    assert launched == [("notepad.exe",)]
    assert run == [("whoami.exe",)]
    assert not permissions.is_granted(Capability.APPLICATION_OPEN)
    assert not permissions.is_granted(Capability.COMMAND_RUN)

    with pytest.raises(ValueError, match="must be one of"):
        executor.execute_once(ToolCall("application.open", {"name": "powershell"}))
    with pytest.raises(ValueError, match="must be one of"):
        executor.execute_once(ToolCall("command.run", {"command": "format"}))


def test_windows_window_actions_are_separate_capabilities(tmp_path, monkeypatch) -> None:
    activated: list[int] = []
    monkeypatch.setattr(
        "babyai.windows_actions._visible_windows",
        lambda: [{"handle": 101, "title": "Notes"}],
    )
    monkeypatch.setattr("babyai.windows_actions._activate_window", activated.append)
    permissions = PermissionStore(tmp_path / "permissions.json")
    executor = AgentExecutor(permissions)

    windows = json.loads(executor.execute_once(ToolCall("window.list", {})))
    executor.execute_once(ToolCall("window.activate", {"handle": 101}))

    assert windows == [{"handle": 101, "title": "Notes"}]
    assert activated == [101]
    assert not permissions.is_granted(Capability.WINDOW_LIST)
    assert not permissions.is_granted(Capability.WINDOW_ACTIVATE)


def test_system_lock_has_a_specific_prompt_and_reject_does_not_call_windows(tmp_path, monkeypatch) -> None:
    called = False

    def lock() -> None:
        nonlocal called
        called = True

    monkeypatch.setattr("babyai.windows_actions._lock_workstation", lock)
    approvals = PendingToolApprovalStore(tmp_path / "pending_tool_approval.json")
    permissions = PermissionStore(tmp_path / "permissions.json")
    primus = Primus(
        llm=EchoProvider(),
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=approvals,
    )

    reply = primus._execute_or_request_approval(
        "base",
        "Заблокируй компьютер",
        ToolCall("system.lock", {}),
    )

    assert "заблокировать рабочую станцию" in reply
    assert approvals.load() is not None
    primus.reject_pending_tool()
    assert called is False
    assert not permissions.is_granted(Capability.SYSTEM_LOCK)


def test_action_tools_reject_unexpected_arguments(tmp_path) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    permissions.grant(Capability.APPLICATION_OPEN)

    with pytest.raises(ToolProtocolError, match="unexpected arguments"):
        AgentExecutor(permissions).execute(
            ToolCall("application.open", {"name": "notepad", "arguments": ["secret.txt"]})
        )


def test_action_intent_gate_stays_explicit(tmp_path) -> None:
    executor = AgentExecutor(PermissionStore(tmp_path / "permissions.json"))

    assert executor.tool_compatible_with_intent("Открой блокнот", "application.open")
    assert executor.tool_compatible_with_intent("Запусти whoami", "command.run")
    assert executor.tool_compatible_with_intent("Заблокируй компьютер", "system.lock")
    assert not executor.tool_compatible_with_intent("Расскажи про блокноты", "application.open")
