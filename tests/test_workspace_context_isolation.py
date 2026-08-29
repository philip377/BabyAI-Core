from __future__ import annotations

from babyai.config import BabyAIConfig
from babyai.history import ChatHistoryStore
from babyai.identity import Identity
from babyai.llm import LLMProvider
from babyai.memory import MemoryKind, SessionMemoryStore, SQLiteMemoryStore
from babyai.permissions import Capability
from babyai.tool_approval import PendingToolApproval, PendingToolApprovalStore
from babyai.workspace import WorkspaceRecord
from babyai.workspace_context import WorkspacePrimus
from babyai.workspace_desktop_commands import WorkspaceDesktopCommands


class CapturingProvider(LLMProvider):
    def __init__(self, reply: str = "ok") -> None:
        self.reply = reply
        self.prompts: list[str] = []

    def generate(self, prompt: str) -> str:
        self.prompts.append(prompt)
        return self.reply


def test_workspace_context_is_present_without_an_active_task(tmp_path) -> None:
    memory = SQLiteMemoryStore(tmp_path / "memory.sqlite3")
    memory.add(
        "owner",
        "Alpha keeps its own durable note.",
        kind=MemoryKind.PROJECT,
        scope="Alpha",
    )
    memory.add(
        "owner",
        "Beta must stay outside Alpha.",
        kind=MemoryKind.PROJECT,
        scope="Beta",
    )
    provider = CapturingProvider()
    workspace = WorkspaceRecord(
        id="a" * 32,
        name="Alpha",
        root=None,
        created_at="2026-08-29T00:00:00+00:00",
    )
    core = WorkspacePrimus(
        llm=provider,
        memory=memory,
        identity=Identity(),
        session_memory=SessionMemoryStore(),
        workspace=workspace,
    )

    assert core.think("Что мы сейчас делаем?") == "ok"

    prompt = provider.prompts[-1]
    assert "Active workspace:" in prompt
    assert "Name: Alpha" in prompt
    assert "Alpha keeps its own durable note." in prompt
    assert "Beta must stay outside Alpha." not in prompt


def test_active_workspace_isolates_tasks_history_memory_and_session(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path, provider="echo")
    commands = WorkspaceDesktopCommands(config, persistent=True)
    commands.execute("history.set_enabled", {"enabled": True})

    alpha = commands.execute("workspace.create", {"name": "Alpha"})["workspace"]
    beta = commands.execute("workspace.create", {"name": "Beta"})["workspace"]

    commands.execute("workspace.select", {"id": alpha["id"]})
    alpha_task = commands.execute("task.set", {"goal": "Alpha task"})["task"]
    assert alpha_task["project"] == "Alpha"
    commands.execute(
        "memory.save",
        {"kind": "project", "content": "Only Alpha should remember this."},
    )
    try:
        commands.execute(
            "memory.save",
            {
                "kind": "project",
                "project": "Beta",
                "content": "This must not cross the active workspace boundary.",
            },
        )
    except ValueError as exc:
        assert "isolated to the active workspace" in str(exc)
    else:
        raise AssertionError("Cross-workspace project memory write was accepted")
    commands.execute("chat", {"message": "alpha-message"})

    alpha_task_file = config.workspace_tasks_dir / f"{alpha['id']}.json"
    assert alpha_task_file.exists()
    assert not config.working_memory_file.exists()

    commands.execute("workspace.select", {"id": beta["id"]})
    beta_status = commands.execute("status")["snapshot"]
    assert beta_status["workspace"]["name"] == "Beta"
    assert beta_status["task"] is None

    beta_task = commands.execute("task.set", {"goal": "Beta task"})["task"]
    assert beta_task["project"] == "Beta"
    commands.execute(
        "memory.save",
        {"kind": "project", "content": "Only Beta should remember this."},
    )
    commands.execute("chat", {"message": "beta-message"})

    beta_memories = commands.execute("memory.list", {"kind": "project"})["memories"]
    assert [item["content"] for item in beta_memories] == [
        "Only Beta should remember this."
    ]

    commands.execute("workspace.select", {"id": alpha["id"]})
    alpha_status = commands.execute("status")["snapshot"]
    assert alpha_status["task"]["goal"] == "Alpha task"
    alpha_memories = commands.execute("memory.list", {"kind": "project"})["memories"]
    assert [item["content"] for item in alpha_memories] == [
        "Only Alpha should remember this."
    ]

    history = ChatHistoryStore(config.history_db, config.history_settings_file)
    alpha_history = history.list(project="Alpha")
    beta_history = history.list(project="Beta")
    assert any("alpha-message" in item.content for item in alpha_history)
    assert all("beta-message" not in item.content for item in alpha_history)
    assert any("beta-message" in item.content for item in beta_history)
    assert all("alpha-message" not in item.content for item in beta_history)

    assert set(commands._session_memories) == {alpha["id"], beta["id"]}
    alpha_session = commands._session_memories[alpha["id"]].recent(limit=20)
    beta_session = commands._session_memories[beta["id"]].recent(limit=20)
    assert any(item.content == "alpha-message" for item in alpha_session)
    assert all(item.content != "beta-message" for item in alpha_session)
    assert any(item.content == "beta-message" for item in beta_session)
    assert all(item.content != "alpha-message" for item in beta_session)


def test_workspace_switch_is_blocked_while_local_action_waits_for_approval(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path, provider="echo")
    commands = WorkspaceDesktopCommands(config)
    alpha = commands.execute("workspace.create", {"name": "Alpha"})["workspace"]
    beta = commands.execute("workspace.create", {"name": "Beta"})["workspace"]
    commands.execute("workspace.select", {"id": alpha["id"]})

    PendingToolApprovalStore(config.pending_tool_approval_file).save(
        PendingToolApproval(
            user_input="Посмотри рабочий стол",
            tool="filesystem.list",
            arguments={"path": "~/Desktop"},
            capability=Capability.FILESYSTEM_LIST.value,
        )
    )

    try:
        commands.execute("workspace.select", {"id": beta["id"]})
    except ValueError as exc:
        assert "pending local action" in str(exc)
    else:
        raise AssertionError("Workspace switched while an approval was still pending")

    current = commands.execute("workspace.current")["workspace"]
    assert current["id"] == alpha["id"]


def test_no_workspace_preserves_legacy_task_and_project_memory_behavior(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path, provider="echo")
    commands = WorkspaceDesktopCommands(config)

    task = commands.execute(
        "task.set",
        {"goal": "Legacy task", "project": "Legacy project"},
    )["task"]
    assert task["project"] == "Legacy project"
    assert config.working_memory_file.exists()

    memory = commands.execute(
        "memory.save",
        {
            "kind": "project",
            "project": "Legacy project",
            "content": "Legacy project memory.",
        },
    )["memory"]
    assert memory["scope"] == "Legacy project"

    listed = commands.execute(
        "memory.list",
        {"kind": "project", "project": "Legacy project"},
    )["memories"]
    assert [item["content"] for item in listed] == ["Legacy project memory."]


def test_desktop_entrypoints_use_workspace_aware_command_surface() -> None:
    from pathlib import Path

    root = Path(__file__).resolve().parents[1]
    worker = (root / "src" / "babyai" / "desktop_worker.py").read_text(
        encoding="utf-8"
    )
    cli = (root / "src" / "babyai" / "desktop_commands_cli.py").read_text(
        encoding="utf-8"
    )

    assert "WorkspaceDesktopCommands as DesktopCommands" in worker
    assert "DesktopCommands(persistent=True)" in worker
    assert "WorkspaceDesktopCommands as DesktopCommands" in cli
    assert "DesktopCommands().execute(command, data)" in cli
