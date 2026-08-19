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


def test_malformed_tool_discussion_gets_one_structured_repair(tmp_path) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "pending_tool_approval.json")
    provider = ScriptedProvider([
        "I need to use filesystem.list to inspect their desktop, but I should ask for permission first.",
        '{"tool":"filesystem.list","arguments":{"path":"~/Desktop"}}',
    ])
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=approvals,
        repair_tool_calls=True,
    )

    reply = primus.think("Назови любой файл на моём рабочем столе")

    assert "разреш" in reply.lower()
    pending = approvals.load()
    assert pending is not None
    assert pending.tool == "filesystem.list"
    assert pending.arguments == {"path": "~/Desktop"}
    assert len(provider.prompts) == 2
    assert "Return exactly one JSON object now" in provider.prompts[-1]


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


def test_native_fast_path_requests_desktop_permission_without_model_pass(tmp_path) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "pending_tool_approval.json")
    provider = ScriptedProvider([])
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=approvals,
        repair_tool_calls=True,
    )

    reply = primus.think("Мог бы ты назвать любое имя файла на моём рабочем столе?")

    assert "разреш" in reply.casefold()
    assert provider.prompts == []
    pending = approvals.load()
    assert pending is not None
    assert pending.tool == "filesystem.list"
    assert pending.arguments == {"path": "~/Desktop"}
    assert not permissions.is_granted(Capability.FILESYSTEM_LIST)


def test_fast_path_stays_narrow_for_ambiguous_desktop_chat(tmp_path) -> None:
    executor = AgentExecutor(PermissionStore(tmp_path / "permissions.json"))

    assert executor.infer_safe_local_intent("Расскажи, что такое рабочий стол Windows") is None


def test_general_identity_question_hides_catalog_and_blocks_hallucinated_tool(tmp_path) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "pending_tool_approval.json")
    provider = ScriptedProvider([])
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=approvals,
        repair_tool_calls=True,
    )

    reply = primus.think("Кто ты и чем можешь помочь?")

    assert reply.startswith("Я BabyAI")
    assert "персональный ИИ-помощник" in reply
    assert provider.prompts == []
    assert approvals.load() is None
    assert not permissions.is_granted(Capability.PROCESS_LIST)
    assert '"tool"' not in reply


def test_identity_and_safety_followup_never_enter_native_tool_loop(tmp_path) -> None:
    provider = ScriptedProvider([])
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(PermissionStore(tmp_path / "permissions.json")),
        repair_tool_calls=True,
    )

    first = primus.think("Кто ты и чем можешь помочь?")
    second = primus.think('Что значит "безопасно"?')

    assert "персональный ИИ-помощник" in first
    assert "без скрытых действий" in second
    assert "не смог безопасно сформировать" not in first + second
    assert '"tool"' not in first + second
    assert provider.prompts == []


def test_repeated_hallucinated_tool_json_uses_non_tool_fallback(tmp_path) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "pending_tool_approval.json")
    provider = ScriptedProvider([
        '{"tool":"process.list","arguments":{}}',
        '{"tool":"process.list","arguments":{}}',
    ])
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=approvals,
    )

    reply = primus.think("Расскажи короткую шутку")

    assert reply == "Я не буду выполнять неподходящее локальное действие. Чем ещё могу помочь?"
    assert approvals.load() is None
    assert not permissions.is_granted(Capability.PROCESS_LIST)
    assert '"tool"' not in reply


def test_tool_call_must_match_the_users_local_intent(tmp_path) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "pending_tool_approval.json")
    provider = ScriptedProvider([
        '{"tool":"process.list","arguments":{}}',
        "Не могу определить файлы без просмотра указанной папки.",
    ])
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=approvals,
    )

    reply = primus.think("Какие файлы находятся в этой папке?")

    assert "process.list" not in reply
    assert approvals.load() is None
    assert not permissions.is_granted(Capability.PROCESS_LIST)


def test_tool_followup_does_not_repeat_catalog(tmp_path) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    permissions.grant(Capability.SYSTEM_INFO)
    provider = ScriptedProvider([
        '{"tool":"system.info","arguments":{}}',
        "На этом компьютере установлена Windows.",
    ])
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
    )

    primus.think("Покажи сведения о компьютере")

    assert "Available tools:" in provider.prompts[0]
    assert "Available tools:" not in provider.prompts[1]


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
    assert 'repair_tool_calls=self.config.provider == "native"' in commands
    assert '"tool_approval"' in snapshot
    assert 'Loaded="Root_Loaded_WithToolApproval"' in xaml
    assert "Root_Loaded(sender, e);" in window
    assert 'ApproveButton.Content = "Разрешить один раз"' in window
