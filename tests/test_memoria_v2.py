import sqlite3

from babyai.identity import Identity, IdentityStore
from babyai.memory import MemoryKind, SQLiteMemoryStore


def test_typed_memory_and_search(tmp_path) -> None:
    memory = SQLiteMemoryStore(tmp_path / "memory.sqlite3")
    memory.add("owner", "My favorite engine is Unreal Engine", kind=MemoryKind.FACT)
    memory.add("owner", "Vector databases can support semantic recall", kind=MemoryKind.KNOWLEDGE)
    memory.add("user", "hello", kind=MemoryKind.EPISODIC)

    facts = memory.recent(kind=MemoryKind.FACT)
    assert len(facts) == 1
    assert facts[0].content == "My favorite engine is Unreal Engine"

    results = memory.search("Unreal", kind=MemoryKind.FACT)
    assert len(results) == 1
    assert results[0].kind is MemoryKind.FACT


def test_legacy_database_is_migrated(tmp_path) -> None:
    db_path = tmp_path / "legacy.sqlite3"
    with sqlite3.connect(db_path) as connection:
        connection.execute(
            """
            CREATE TABLE memories (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL
            )
            """
        )
        connection.execute(
            "INSERT INTO memories(role, content, created_at) VALUES (?, ?, ?)",
            ("user", "old memory", "2026-01-01T00:00:00+00:00"),
        )

    memory = SQLiteMemoryStore(db_path)
    records = memory.recent()

    assert len(records) == 1
    assert records[0].kind is MemoryKind.EPISODIC
    assert records[0].content == "old memory"


def test_identity_survives_restart(tmp_path) -> None:
    path = tmp_path / "identity.json"
    store = IdentityStore(path)
    expected = Identity(name="BabyAI", owner="KiRiYaN", purpose="Grow together")

    first = store.load_or_create(expected)
    second = IdentityStore(path).load_or_create(Identity(owner="someone else"))

    assert first == expected
    assert second == expected
