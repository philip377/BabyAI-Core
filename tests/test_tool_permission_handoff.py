from __future__ import annotations

from collections import deque

from babyai.agent import AgentExecutor
from babyai.identity import Identity
from babyai.llm import LLMProvider
from babyai.memory import SQLiteMemoryStore
from babyai.permissions import Capability, PermissionStore
from babyai.primus import Primus
from babyai.tool_approval import PendingToolApprovalStore


class ScriptedProvider(LLMProvider):
    def __init__(self, responses: list[str]) -> None:
        self.responses = deque(responses)
        self.prompts: list[str] = []

    def generate(self, prompt: str) -> str:
        self.prompts.append(prompt)
        return self.responses.popleft()


def test_tool_call_can_be_extracted_after_model_prose(tmp_path) -> None:
    executor = AgentExecutor(PermissionStore(tmp_path / "permissions.json"))
    call = executor.parse_tool_call(
        'I should inspect the folder first.\n'
        '{"tool":"filesystem.list","arguments":{"path":"~/Desktop"}}'
    )

    assert call is not None
    assert call.name == "filesystem.list"
    assert call.arguments == {"path": "~/Desktop"}


def test_tool_permission_pauses_then_executes_once(tmp_path) -> None:
    folder = tmp_path / "Desktop"
    folder.mkdir()
    (folder / "example.txt").write_text("hello", encoding="utf-8")

    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "pending_tool_approval.json")
    provider = ScriptedProvider([
        '{"tool":"filesystem.list","arguments":{"path":"%s"}}' % folder.as_posix(),
        "Например: example.txt",
    ])
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=approvals,
    )

    first = primus.think("Назови любой файл в этой папке")

    assert "разреш" in first.lower()
    pending = approvals.load()
    assert pending is not None
    assert pending.tool == "filesystem.list"
    assert pending.capability == Capability.FILESYSTEM_LIST.value
    assert not permissions.is_granted(Capability.FILESYSTEM_LIST)

    final = primus.approve_pending_tool()

    assert final == "Например: example.txt"
    assert approvals.load() is None
    assert not permissions.is_granted(Capability.FILESYSTEM_LIST)
    assert "example.txt" in provider.prompts[-1]


def test_reject_pending_tool_does_not_execute_or_grant(tmp_path) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "pending_tool_approval.json")
    provider = ScriptedProvider([
        '{"tool":"filesystem.list","arguments":{"path":"~/Desktop"}}',
    ])
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=approvals,
    )

    primus.think("Посмотри рабочий стол")
    reply = primus.reject_pending_tool()

    assert "не выполнял" in reply.lower()
    assert approvals.load() is None
    assert not permissions.is_granted(Capability.FILESYSTEM_LIST)
    assert len(provider.prompts) == 1


def test_desktop_contract_surfaces_tool_approval_controls() -> None:
    from pathlib import Path

    root = Path(__file__).resolve().parents[1]
    bridge = (root / "desktop" / "BabyAI.Desktop" / "BabyAIBridgeClient.cs").read_text(encoding="utf-8")
    window = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.ToolApproval.cs").read_text(encoding="utf-8")
    xaml = (root / "desktop" / "BabyAI.Desktop" / "MainWindow.xaml").read_text(encoding="utf-8")
    commands = (root / "src" / "babyai" / "desktop_commands.py").read_text(encoding="utf-8")
    snapshot = (root / "src" / "babyai" / "desktop_bridge.py").read_text(encoding="utf-8")

    assert 'TryGetProperty("tool_approval"' in bridge
    assert "ApproveToolAsync" in bridge
    assert "RejectToolAsync" in bridge
    assert 'ExecuteReplyCommandAsync("approval.approve")' in bridge
    assert 'command == "approval.approve"' in commands
    assert 'command == "approval.reject"' in commands
    assert '"tool_approval"' in snapshot
    assert 'Loaded="Root_Loaded"' in xaml
    assert 'ApproveButton.Content = "Разрешить один раз"' in window
