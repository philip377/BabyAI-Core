from __future__ import annotations

import sqlite3
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Protocol


@dataclass(slots=True)
class MemoryRecord:
    id: int | None
    role: str
    content: str
    created_at: datetime


class MemoryStore(Protocol):
    def add(self, role: str, content: str) -> MemoryRecord: ...
    def recent(self, limit: int = 10) -> list[MemoryRecord]: ...


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

    def add(self, role: str, content: str) -> MemoryRecord:
        created_at = datetime.now(timezone.utc)
        with self._connect() as connection:
            cursor = connection.execute(
                "INSERT INTO memories(role, content, created_at) VALUES (?, ?, ?)",
                (role, content, created_at.isoformat()),
            )
            memory_id = int(cursor.lastrowid)
        return MemoryRecord(memory_id, role, content, created_at)

    def recent(self, limit: int = 10) -> list[MemoryRecord]:
        with self._connect() as connection:
            rows = connection.execute(
                "SELECT id, role, content, created_at FROM memories ORDER BY id DESC LIMIT ?",
                (limit,),
            ).fetchall()
        return [
            MemoryRecord(
                id=int(row["id"]),
                role=str(row["role"]),
                content=str(row["content"]),
                created_at=datetime.fromisoformat(str(row["created_at"])),
            )
            for row in reversed(rows)
        ]
