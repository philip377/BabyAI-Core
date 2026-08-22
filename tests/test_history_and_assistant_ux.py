from __future__ import annotations

from pathlib import Path

import pytest

from babyai.agent import AgentExecutor, ToolCall
from babyai.config import BabyAIConfig
from babyai.desktop_bridge import build_desktop_snapshot
from babyai.desktop_commands import DesktopCommands
from babyai.history import ChatHistoryStore
from babyai.identity import Identity
from babyai.llm import EchoProvider
from babyai.memory import SQLiteMemoryStore
from babyai.permissions import Capability, PermissionStore
from babyai.primus import Primus
from babyai.tool_approval import PendingToolApproval, PendingToolApprovalStore
from babyai.working_memory import TaskState, WorkingMemoryStore


def test_history_is_opt_in_and_project_scoped(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path, provider="echo")
    commands = DesktopCommands(config, persistent=True)
    tasks = WorkingMemoryStore(config.working_memory_file)
    tasks.save(TaskState(goal="Build", project="BabyAI"))

    commands.execute("chat", {"message": "not persisted"})
    assert not config.history_db.exists()

    commands.execute("history.set_enabled", {"enabled": True})
    commands.execute("chat", {"message": "persist this"})

    listed = commands.execute("history.list", {"project": "BabyAI"})
    assert listed["enabled"] is True
    assert [item["role"] for item in listed["messages"]] == ["user", "babyai"]
    assert all(item["project"] == "BabyAI" for item in listed["messages"])
    assert build_desktop_snapshot(config).as_dict()["history"]["message_count"] == 2

    cleared = commands.execute("history.clear", {"project": "BabyAI"})
    assert cleared["deleted"] == 2
    assert ChatHistoryStore(config.history_db, config.history_settings_file).list() == []


def test_pending_approval_is_consumed_before_execution_can_be_cancelled(tmp_path, monkeypatch) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "pending.json")
    approvals.save(
        PendingToolApproval(
            user_input="Открой блокнот",
            tool="application.open",
            arguments={"name": "notepad"},
            capability=Capability.APPLICATION_OPEN.value,
        )
    )
    executor = AgentExecutor(permissions)
    monkeypatch.setattr(
        AgentExecutor,
        "execute_once",
        lambda self, call: (_ for _ in ()).throw(RuntimeError("cancelled worker")),
    )
    primus = Primus(
        llm=EchoProvider(),
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=executor,
        tool_approvals=approvals,
    )

    with pytest.raises(RuntimeError, match="cancelled worker"):
        primus.approve_pending_tool()

    assert approvals.load() is None
    assert not permissions.is_granted(Capability.APPLICATION_OPEN)


def test_desktop_has_explicit_localized_assistant_states() -> None:
    root = Path(__file__).resolve().parents[1]
    window = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.xaml.cs").read_text(
        encoding="utf-8"
    )
    approval = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.ToolApproval.cs").read_text(
        encoding="utf-8"
    )
    adaptive = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.AdaptiveUi.cs").read_text(
        encoding="utf-8"
    )
    xaml = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.xaml").read_text(
        encoding="utf-8"
    )

    assert "OrbState.Thinking" in window
    assert "OrbState.Executing" in window
    assert "OrbState.Approval" in window
    assert "OrbState.Done" in window
    assert "OrbState.Error" in window
    assert 'ReplyText.Text = "Думаю…"' in window
    assert 'ReplyText.Text = "Готово."' in window
    assert 'ReplyText.Text = "Действие отменено."' in window
    assert "ApproveToolAsync(_chatCancellation.Token)" in approval
    assert "BabyAI · выполняю" in adaptive
    assert "BabyAI · ждёт решения" in adaptive
    assert 'x:Name="ApprovalDescriptionText"' in xaml
    assert "Thinking…" not in window
    assert "Response complete." not in window


def test_desktop_history_controls_are_explicit_and_default_off() -> None:
    root = Path(__file__).resolve().parents[1]
    settings = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.Settings.cs").read_text(
        encoding="utf-8"
    )
    bridge = (root / "desktop" / "BabyAI.Desktop" / "BabyAIBridgeClient.cs").read_text(
        encoding="utf-8"
    )

    assert "Сохранять локальную историю чата" in settings
    assert "Удалить всю историю" in settings
    assert "SetHistoryEnabledAsync" in bridge
    assert "ClearHistoryAsync" in bridge
