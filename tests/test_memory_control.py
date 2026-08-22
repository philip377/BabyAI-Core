from __future__ import annotations

import sqlite3

from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommands
from babyai.identity import Identity
from babyai.llm import LLMProvider
from babyai.memory import MemoryKind, SessionMemoryStore, SQLiteMemoryStore
from babyai.primus import Primus
from babyai.working_memory import TaskState, WorkingMemoryStore


class CaptureProvider(LLMProvider):
    def __init__(self) -> None:
        self.prompts: list[str] = []

    def generate(self, prompt: str) -> str:
        self.prompts.append(prompt)
        return "ok"


def test_memory_crud_and_project_scope(tmp_path) -> None:
    memory = SQLiteMemoryStore(tmp_path / "memory.sqlite3")
    preference = memory.add(
        "owner",
        "Отвечай кратко",
        kind=MemoryKind.PREFERENCE,
        scope="global",
    )
    alpha = memory.add("owner", "alpha fact", kind=MemoryKind.PROJECT, scope="alpha")
    memory.add("owner", "beta fact", kind=MemoryKind.PROJECT, scope="beta")

    assert [item.content for item in memory.recent(kind=MemoryKind.PROJECT, scope="alpha")] == [
        "alpha fact"
    ]
    updated = memory.update(preference.id or 0, "Отвечай очень кратко")
    assert updated.content == "Отвечай очень кратко"
    assert memory.delete(alpha.id or 0)
    assert memory.recent(kind=MemoryKind.PROJECT, scope="alpha") == []
    assert not memory.delete(999_999)


def test_legacy_memory_database_migrates_to_global_scope(tmp_path) -> None:
    path = tmp_path / "memory.sqlite3"
    with sqlite3.connect(path) as connection:
        connection.execute(
            "CREATE TABLE memories ("
            "id INTEGER PRIMARY KEY AUTOINCREMENT, role TEXT NOT NULL, "
            "content TEXT NOT NULL, created_at TEXT NOT NULL)"
        )
        connection.execute(
            "INSERT INTO memories(role, content, created_at) VALUES (?, ?, ?)",
            ("owner", "legacy", "2026-01-01T00:00:00+00:00"),
        )

    record = SQLiteMemoryStore(path).recent()[0]

    assert record.content == "legacy"
    assert record.kind is MemoryKind.EPISODIC
    assert record.scope == "global"


def test_session_memory_is_bounded_and_process_local() -> None:
    session = SessionMemoryStore(max_records=3)
    for index in range(5):
        session.add("user", f"turn-{index}")

    assert [item.content for item in session.recent(limit=10)] == [
        "turn-2",
        "turn-3",
        "turn-4",
    ]
    session.clear()
    assert session.recent() == []


def test_desktop_chat_does_not_persist_every_turn(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path, provider="echo")
    commands = DesktopCommands(config, persistent=True)

    commands.execute("chat", {"message": "обычный разговор"})

    assert SQLiteMemoryStore(config.memory_db).recent(kind=MemoryKind.EPISODIC) == []
    assert len(commands._session_memory.recent(kind=MemoryKind.EPISODIC)) == 2


def test_prompt_uses_global_preferences_and_only_the_active_project(tmp_path) -> None:
    memory = SQLiteMemoryStore(tmp_path / "memory.sqlite3")
    memory.add("owner", "Пиши по-русски", kind=MemoryKind.PREFERENCE)
    memory.add("owner", "alpha-only", kind=MemoryKind.PROJECT, scope="alpha")
    memory.add("owner", "beta-only", kind=MemoryKind.PROJECT, scope="beta")
    tasks = WorkingMemoryStore(tmp_path / "working.json")
    tasks.save(TaskState(goal="Build", project="alpha"))
    provider = CaptureProvider()
    primus = Primus(
        llm=provider,
        memory=memory,
        session_memory=SessionMemoryStore(),
        identity=Identity(),
        working_memory=tasks,
    )

    primus.think("продолжай")

    prompt = provider.prompts[0]
    assert "Пиши по-русски" in prompt
    assert "alpha-only" in prompt
    assert "beta-only" not in prompt


def test_desktop_memory_commands_allow_view_edit_and_delete(tmp_path) -> None:
    commands = DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider="echo"))

    saved = commands.execute(
        "memory.save",
        {"kind": "project", "project": "BabyAI", "content": "Use Vulkan"},
    )["memory"]
    listed = commands.execute("memory.list", {"project": "BabyAI"})["memories"]
    updated = commands.execute(
        "memory.update",
        {"id": saved["id"], "content": "Prefer Vulkan auto"},
    )["memory"]
    commands.execute("memory.delete", {"id": saved["id"]})

    assert listed[0]["scope"] == "BabyAI"
    assert updated["content"] == "Prefer Vulkan auto"
    assert commands.execute("memory.list", {"project": "BabyAI"})["memories"] == []
