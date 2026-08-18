from __future__ import annotations

from babyai.agent import AgentExecutor
from babyai.identity import Identity
from babyai.llm import LLMProvider
from babyai.memory import SQLiteMemoryStore
from babyai.permissions import Capability, PermissionStore
from babyai.primus import Primus
from babyai.tool_approval import PendingToolApprovalStore


class NoGenerationProvider(LLMProvider):
    def __init__(self) -> None:
        self.prompts: list[str] = []

    def generate(self, prompt: str) -> str:
        self.prompts.append(prompt)
        raise AssertionError("native generation should not run for deterministic desktop listing")


def test_desktop_listing_approval_finishes_without_second_llm_pass(tmp_path, monkeypatch) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "pending_tool_approval.json")
    provider = NoGenerationProvider()
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=approvals,
        repair_tool_calls=True,
    )

    first = primus.think("Мог бы ты назвать любое имя файла на моём рабочем столе?")
    assert "разреш" in first.casefold()
    assert provider.prompts == []

    monkeypatch.setattr(
        primus.agent,
        "execute_once",
        lambda call: '["Folder/", "example.txt", "notes.md"]',
    )

    final = primus.approve_pending_tool()

    assert final == "Например: example.txt"
    assert provider.prompts == []
    assert approvals.load() is None
    assert not permissions.is_granted(Capability.FILESYSTEM_LIST)


def test_empty_desktop_listing_finishes_without_llm_pass(tmp_path, monkeypatch) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "pending_tool_approval.json")
    provider = NoGenerationProvider()
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=approvals,
        repair_tool_calls=True,
    )

    primus.think("Назови любой файл на рабочем столе")
    monkeypatch.setattr(primus.agent, "execute_once", lambda call: '["OnlyFolder/"]')

    final = primus.approve_pending_tool()

    assert final == "На рабочем столе я не нашёл файлов."
    assert provider.prompts == []
