from __future__ import annotations

import json
import sqlite3
import uuid
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
    chat_id: str | None = None

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
        if "chat_id" not in {row[1] for row in connection.execute("PRAGMA table_info(history)")}:
            connection.execute("ALTER TABLE history ADD COLUMN chat_id TEXT")
        connection.execute(
            "CREATE TABLE IF NOT EXISTS chats (id TEXT PRIMARY KEY, scope TEXT NOT NULL, "
            "project TEXT NOT NULL, title TEXT NOT NULL, updated_at TEXT NOT NULL)"
        )
        connection.execute(
            "CREATE TABLE IF NOT EXISTS chat_selection (scope TEXT PRIMARY KEY, chat_id TEXT NOT NULL)"
        )
        connection.execute("CREATE INDEX IF NOT EXISTS idx_history_chat ON history(chat_id, id)")
        connection.commit()
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
                "INSERT INTO history(project, role, content, created_at, chat_id) VALUES (?, ?, ?, ?, ?)",
                (project, role, content, created_at, self.chat_id),
            )
            message_id = int(cursor.lastrowid)
            if self.chat_id:
                title = " ".join(content.split())[:64] if role == "user" else "Новый чат"
                connection.execute(
                    "UPDATE chats SET updated_at = ?, title = CASE WHEN title = 'Новый чат' "
                    "THEN ? ELSE title END WHERE id = ?",
                    (created_at, title, self.chat_id),
                )
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
                connection.execute("UPDATE chats SET title = 'Новый чат'")
            else:
                cursor = connection.execute(
                    "DELETE FROM history WHERE project = ?",
                    (project.strip(),),
                )
                connection.execute("UPDATE chats SET title = 'Новый чат' WHERE project = ?", (project.strip(),))
        return cursor.rowcount

    def current_chat(self, scope: str, project: str) -> str:
        with self._connect() as connection:
            row = connection.execute(
                "SELECT c.id FROM chats c JOIN chat_selection s ON c.id = s.chat_id "
                "WHERE s.scope = ? AND c.scope = ?", (scope, scope),
            ).fetchone()
            if row:
                return row[0]
        return self.create_chat(scope, project, migrate=True)

    def create_chat(self, scope: str, project: str, *, migrate: bool = False) -> str:
        chat_id = uuid.uuid4().hex
        with self._connect() as connection:
            connection.execute("INSERT INTO chats VALUES (?, ?, ?, ?, ?)",
                               (chat_id, scope, project, "Новый чат", datetime.now(timezone.utc).isoformat()))
            if migrate:
                # Claim legacy rows once, in place, without copying transcripts.
                connection.execute("UPDATE history SET chat_id = ? WHERE project = ? AND chat_id IS NULL",
                                   (chat_id, project))
                first = connection.execute(
                    "SELECT content FROM history WHERE chat_id = ? AND role = 'user' ORDER BY id LIMIT 1",
                    (chat_id,),
                ).fetchone()
                if first:
                    connection.execute("UPDATE chats SET title = ? WHERE id = ?",
                                       (" ".join(first[0].split())[:64], chat_id))
            connection.execute("INSERT OR REPLACE INTO chat_selection VALUES (?, ?)", (scope, chat_id))
        return chat_id

    def select_chat(self, scope: str, chat_id: str) -> None:
        with self._connect() as connection:
            if not connection.execute("SELECT 1 FROM chats WHERE id = ? AND scope = ?",
                                      (chat_id, scope)).fetchone():
                raise ValueError("Chat does not belong to the active workspace")
            connection.execute("INSERT OR REPLACE INTO chat_selection VALUES (?, ?)", (scope, chat_id))

    def chats(self, scope: str) -> list[dict[str, object]]:
        with self._connect() as connection:
            return [dict(row) for row in connection.execute(
                "SELECT id, title, updated_at FROM chats WHERE scope = ? ORDER BY updated_at DESC, rowid DESC",
                (scope,),
            )]

    def chat_messages(self, chat_id: str, *, limit: int = 500) -> list[HistoryMessage]:
        with self._connect() as connection:
            rows = connection.execute(
                "SELECT id, project, role, content, created_at FROM history "
                "WHERE chat_id = ? ORDER BY id DESC LIMIT ?", (chat_id, limit),
            ).fetchall()
        return [HistoryMessage(**dict(row)) for row in reversed(rows)]
