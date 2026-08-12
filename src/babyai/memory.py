from __future__ import annotations

import sqlite3
from dataclasses import dataclass
from datetime import datetime, timezone
from enum import StrEnum
from pathlib import Path
from typing import Protocol


class MemoryKind(StrEnum):
    EPISODIC = "episodic"
    FACT = "fact"
    KNOWLEDGE = "knowledge"
    WORKING = "working"


@dataclass(slots=True)
class MemoryRecord:
    id: int | None
    kind: MemoryKind
    role: str
    content: str
    created_at: datetime


class MemoryStore(Protocol):
    def add(
        self,
        role: str,
        content: str,
        *,
        kind: MemoryKind = MemoryKind.EPISODIC,
    ) -> MemoryRecord: ...

    def recent(
        self,
        limit: int = 10,
        *,
        kind: MemoryKind | None = None,
    ) -> list[MemoryRecord]: ...

    def search(
        self,
        query: str,
        *,
        limit: int = 10,
        kind: MemoryKind | None = None,
    ) -> list[MemoryRecord]: ...


class SQLiteMemoryStore:
    def __init__(self, db_path: str | Path) -> None:
        self.db_path = Path(db_path)
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self._init_db()

    def _connect(self) -> sqlite3.Connection:
        connection = sqlite3.connect(self.db_path)
        connection.row_factory = sqlite3.Row
        return connection

    def _init_db(self) -> None:
        with self._connect() as connection:
            connection.execute(
                """
                CREATE TABLE IF NOT EXISTS memories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    role TEXT NOT NULL,
                    content TEXT NOT NULL,
                    created_at TEXT NOT NULL
                )
                """
            )
            columns = {
                str(row["name"])
                for row in connection.execute("PRAGMA table_info(memories)").fetchall()
            }
            if "kind" not in columns:
                connection.execute(
                    "ALTER TABLE memories ADD COLUMN kind TEXT NOT NULL DEFAULT 'episodic'"
                )
            connection.execute(
                "CREATE INDEX IF NOT EXISTS idx_memories_kind_id ON memories(kind, id)"
            )

    @staticmethod
    def _row_to_record(row: sqlite3.Row) -> MemoryRecord:
        return MemoryRecord(
            id=int(row["id"]),
            kind=MemoryKind(str(row["kind"])),
            role=str(row["role"]),
            content=str(row["content"]),
            created_at=datetime.fromisoformat(str(row["created_at"])),
        )

    def add(
        self,
        role: str,
        content: str,
        *,
        kind: MemoryKind = MemoryKind.EPISODIC,
    ) -> MemoryRecord:
        created_at = datetime.now(timezone.utc)
        with self._connect() as connection:
            cursor = connection.execute(
                "INSERT INTO memories(kind, role, content, created_at) VALUES (?, ?, ?, ?)",
                (kind.value, role, content, created_at.isoformat()),
            )
            memory_id = int(cursor.lastrowid)
        return MemoryRecord(memory_id, kind, role, content, created_at)

    def recent(
        self,
        limit: int = 10,
        *,
        kind: MemoryKind | None = None,
    ) -> list[MemoryRecord]:
        with self._connect() as connection:
            if kind is None:
                rows = connection.execute(
                    "SELECT id, kind, role, content, created_at "
                    "FROM memories ORDER BY id DESC LIMIT ?",
                    (limit,),
                ).fetchall()
            else:
                rows = connection.execute(
                    "SELECT id, kind, role, content, created_at "
                    "FROM memories WHERE kind = ? ORDER BY id DESC LIMIT ?",
                    (kind.value, limit),
                ).fetchall()
        return [self._row_to_record(row) for row in reversed(rows)]

    def search(
        self,
        query: str,
        *,
        limit: int = 10,
        kind: MemoryKind | None = None,
    ) -> list[MemoryRecord]:
        pattern = f"%{query}%"
        with self._connect() as connection:
            if kind is None:
                rows = connection.execute(
                    "SELECT id, kind, role, content, created_at "
                    "FROM memories WHERE content LIKE ? ORDER BY id DESC LIMIT ?",
                    (pattern, limit),
                ).fetchall()
            else:
                rows = connection.execute(
                    "SELECT id, kind, role, content, created_at "
                    "FROM memories WHERE kind = ? AND content LIKE ? "
                    "ORDER BY id DESC LIMIT ?",
                    (kind.value, pattern, limit),
                ).fetchall()
        return [self._row_to_record(row) for row in rows]
