from __future__ import annotations

import sqlite3
from collections import deque
from dataclasses import dataclass
from datetime import datetime, timezone
from enum import StrEnum
from pathlib import Path
from typing import Protocol


class MemoryKind(StrEnum):
    EPISODIC = "episodic"
    PREFERENCE = "preference"
    FACT = "fact"
    KNOWLEDGE = "knowledge"
    PROJECT = "project"
    WORKING = "working"


DURABLE_MEMORY_KINDS = frozenset(
    {MemoryKind.PREFERENCE, MemoryKind.FACT, MemoryKind.KNOWLEDGE, MemoryKind.PROJECT}
)


@dataclass(slots=True)
class MemoryRecord:
    id: int | None
    kind: MemoryKind
    role: str
    content: str
    created_at: datetime
    scope: str = "global"


class MemoryStore(Protocol):
    def add(
        self,
        role: str,
        content: str,
        *,
        kind: MemoryKind = MemoryKind.EPISODIC,
        scope: str = "global",
    ) -> MemoryRecord: ...

    def recent(
        self,
        limit: int = 10,
        *,
        kind: MemoryKind | None = None,
        scope: str | None = None,
    ) -> list[MemoryRecord]: ...

    def search(
        self,
        query: str,
        *,
        limit: int = 10,
        kind: MemoryKind | None = None,
        scope: str | None = None,
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
            if "scope" not in columns:
                connection.execute(
                    "ALTER TABLE memories ADD COLUMN scope TEXT NOT NULL DEFAULT 'global'"
                )
            connection.execute(
                "CREATE INDEX IF NOT EXISTS idx_memories_kind_id ON memories(kind, id)"
            )
            connection.execute(
                "CREATE INDEX IF NOT EXISTS idx_memories_kind_scope_id "
                "ON memories(kind, scope, id)"
            )

    @staticmethod
    def _row_to_record(row: sqlite3.Row) -> MemoryRecord:
        return MemoryRecord(
            id=int(row["id"]),
            kind=MemoryKind(str(row["kind"])),
            role=str(row["role"]),
            content=str(row["content"]),
            created_at=datetime.fromisoformat(str(row["created_at"])),
            scope=str(row["scope"]),
        )

    def add(
        self,
        role: str,
        content: str,
        *,
        kind: MemoryKind = MemoryKind.EPISODIC,
        scope: str = "global",
    ) -> MemoryRecord:
        content = content.strip()
        scope = scope.strip() or "global"
        if not content:
            raise ValueError("Memory content cannot be empty")
        created_at = datetime.now(timezone.utc)
        with self._connect() as connection:
            cursor = connection.execute(
                "INSERT INTO memories(kind, scope, role, content, created_at) "
                "VALUES (?, ?, ?, ?, ?)",
                (kind.value, scope, role, content, created_at.isoformat()),
            )
            memory_id = int(cursor.lastrowid)
        return MemoryRecord(memory_id, kind, role, content, created_at, scope)

    def recent(
        self,
        limit: int = 10,
        *,
        kind: MemoryKind | None = None,
        scope: str | None = None,
    ) -> list[MemoryRecord]:
        clauses: list[str] = []
        parameters: list[object] = []
        if kind is not None:
            clauses.append("kind = ?")
            parameters.append(kind.value)
        if scope is not None:
            clauses.append("scope = ?")
            parameters.append(scope)
        where = " WHERE " + " AND ".join(clauses) if clauses else ""
        with self._connect() as connection:
            rows = connection.execute(
                "SELECT id, kind, scope, role, content, created_at "
                f"FROM memories{where} ORDER BY id DESC LIMIT ?",
                (*parameters, limit),
            ).fetchall()
        return [self._row_to_record(row) for row in reversed(rows)]

    def search(
        self,
        query: str,
        *,
        limit: int = 10,
        kind: MemoryKind | None = None,
        scope: str | None = None,
    ) -> list[MemoryRecord]:
        pattern = f"%{query}%"
        clauses = ["content LIKE ?"]
        parameters: list[object] = [pattern]
        if kind is not None:
            clauses.append("kind = ?")
            parameters.append(kind.value)
        if scope is not None:
            clauses.append("scope = ?")
            parameters.append(scope)
        with self._connect() as connection:
            rows = connection.execute(
                "SELECT id, kind, scope, role, content, created_at "
                "FROM memories WHERE "
                + " AND ".join(clauses)
                + " ORDER BY id DESC LIMIT ?",
                (*parameters, limit),
            ).fetchall()
        return [self._row_to_record(row) for row in rows]

    def update(self, memory_id: int, content: str) -> MemoryRecord:
        content = content.strip()
        if not content:
            raise ValueError("Memory content cannot be empty")
        with self._connect() as connection:
            cursor = connection.execute(
                "UPDATE memories SET content = ? WHERE id = ?",
                (content, memory_id),
            )
            if cursor.rowcount != 1:
                raise KeyError(f"Memory #{memory_id} does not exist")
            row = connection.execute(
                "SELECT id, kind, scope, role, content, created_at "
                "FROM memories WHERE id = ?",
                (memory_id,),
            ).fetchone()
        assert row is not None
        return self._row_to_record(row)

    def delete(self, memory_id: int) -> bool:
        with self._connect() as connection:
            cursor = connection.execute("DELETE FROM memories WHERE id = ?", (memory_id,))
        return cursor.rowcount == 1


class SessionMemoryStore:
    """Bounded process-local conversational context; nothing is written to disk."""

    def __init__(self, max_records: int = 48) -> None:
        if max_records <= 0:
            raise ValueError("max_records must be positive")
        self._records: deque[MemoryRecord] = deque(maxlen=max_records)
        self._next_id = 1

    def add(
        self,
        role: str,
        content: str,
        *,
        kind: MemoryKind = MemoryKind.EPISODIC,
        scope: str = "session",
    ) -> MemoryRecord:
        content = content.strip()
        if not content:
            raise ValueError("Memory content cannot be empty")
        record = MemoryRecord(
            self._next_id,
            kind,
            role,
            content,
            datetime.now(timezone.utc),
            scope,
        )
        self._next_id += 1
        self._records.append(record)
        return record

    def recent(
        self,
        limit: int = 10,
        *,
        kind: MemoryKind | None = None,
        scope: str | None = None,
    ) -> list[MemoryRecord]:
        records = [
            item
            for item in self._records
            if (kind is None or item.kind is kind) and (scope is None or item.scope == scope)
        ]
        return records[-limit:]

    def search(
        self,
        query: str,
        *,
        limit: int = 10,
        kind: MemoryKind | None = None,
        scope: str | None = None,
    ) -> list[MemoryRecord]:
        query = query.casefold()
        records = [
            item
            for item in reversed(self._records)
            if query in item.content.casefold()
            and (kind is None or item.kind is kind)
            and (scope is None or item.scope == scope)
        ]
        return records[:limit]

    def clear(self) -> None:
        self._records.clear()
