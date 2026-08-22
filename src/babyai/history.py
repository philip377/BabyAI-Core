from __future__ import annotations

import json
import sqlite3
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path


@dataclass(frozen=True, slots=True)
class HistoryMessage:
    id: int
    project: str
    role: str
    content: str
    created_at: str


@dataclass(slots=True)
class ChatHistoryStore:
    db_path: Path
    settings_path: Path

    def is_enabled(self) -> bool:
        if not self.settings_path.exists():
            return False
        try:
            data = json.loads(self.settings_path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return False
        return data.get("enabled") is True if isinstance(data, dict) else False

    def set_enabled(self, enabled: bool) -> None:
        if not isinstance(enabled, bool):
            raise ValueError("History enabled must be true or false")
        self.settings_path.parent.mkdir(parents=True, exist_ok=True)
        self.settings_path.write_text(
            json.dumps({"enabled": enabled}, indent=2),
            encoding="utf-8",
        )

    def _connect(self) -> sqlite3.Connection:
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        connection = sqlite3.connect(self.db_path)
        connection.row_factory = sqlite3.Row
        connection.execute(
            "CREATE TABLE IF NOT EXISTS history ("
            "id INTEGER PRIMARY KEY AUTOINCREMENT, project TEXT NOT NULL, "
            "role TEXT NOT NULL, content TEXT NOT NULL, created_at TEXT NOT NULL)"
        )
        connection.execute(
            "CREATE INDEX IF NOT EXISTS idx_history_project_id ON history(project, id)"
        )
        return connection

    def add(self, role: str, content: str, *, project: str = "") -> HistoryMessage | None:
        if not self.is_enabled():
            return None
        content = content.strip()
        if not content:
            return None
        created_at = datetime.now(timezone.utc).isoformat()
        project = project.strip()
        with self._connect() as connection:
            cursor = connection.execute(
                "INSERT INTO history(project, role, content, created_at) VALUES (?, ?, ?, ?)",
                (project, role, content, created_at),
            )
            message_id = int(cursor.lastrowid)
        return HistoryMessage(message_id, project, role, content, created_at)

    def list(self, *, project: str | None = None, limit: int = 100) -> list[HistoryMessage]:
        if not self.db_path.exists():
            return []
        with self._connect() as connection:
            if project is None:
                rows = connection.execute(
                    "SELECT id, project, role, content, created_at "
                    "FROM history ORDER BY id DESC LIMIT ?",
                    (limit,),
                ).fetchall()
            else:
                rows = connection.execute(
                    "SELECT id, project, role, content, created_at "
                    "FROM history WHERE project = ? ORDER BY id DESC LIMIT ?",
                    (project.strip(), limit),
                ).fetchall()
        return [HistoryMessage(**dict(row)) for row in reversed(rows)]

    def clear(self, *, project: str | None = None) -> int:
        if not self.db_path.exists():
            return 0
        with self._connect() as connection:
            if project is None:
                cursor = connection.execute("DELETE FROM history")
            else:
                cursor = connection.execute(
                    "DELETE FROM history WHERE project = ?",
                    (project.strip(),),
                )
        return cursor.rowcount
