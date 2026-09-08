from __future__ import annotations

import sqlite3
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path


JOB_STATUSES = frozenset(
    {
        "queued",
        "running",
        "waiting_permission",
        "paused",
        "completed",
        "failed",
        "cancelled",
    }
)


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


@dataclass(frozen=True, slots=True)
class DurableJob:
    id: str
    workspace_id: str | None
    chat_id: str | None
    goal: str
    status: str
    checkpoint: str
    result: str | None
    error: str | None
    cancel_requested: bool
    revision: int
    created_at: str
    updated_at: str


@dataclass(frozen=True, slots=True)
class DurableJobEvent:
    id: int
    job_id: str
    from_status: str | None
    to_status: str
    note: str
    created_at: str


class DurableJobStore:
    """Persist explicit, auditable long-running job state.

    This store never starts work by itself. Interrupted ``running`` jobs are
    recovered to ``paused`` so process restart cannot replay a side effect.
    """

    def __init__(self, db_path: str | Path) -> None:
        self.db_path = Path(db_path)

    def _connect(self) -> sqlite3.Connection:
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        connection = sqlite3.connect(self.db_path, timeout=10)
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA foreign_keys = ON")
        connection.execute(
            "CREATE TABLE IF NOT EXISTS durable_jobs ("
            "id TEXT PRIMARY KEY, workspace_id TEXT, chat_id TEXT, "
            "goal TEXT NOT NULL, status TEXT NOT NULL, checkpoint TEXT NOT NULL DEFAULT '', "
            "result TEXT, error TEXT, cancel_requested INTEGER NOT NULL DEFAULT 0, "
            "revision INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, updated_at TEXT NOT NULL)"
        )
        connection.execute(
            "CREATE INDEX IF NOT EXISTS idx_durable_jobs_scope_updated "
            "ON durable_jobs(workspace_id, updated_at DESC)"
        )
        connection.execute(
            "CREATE TABLE IF NOT EXISTS durable_job_events ("
            "id INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL, "
            "from_status TEXT, to_status TEXT NOT NULL, note TEXT NOT NULL, "
            "created_at TEXT NOT NULL, "
            "FOREIGN KEY(job_id) REFERENCES durable_jobs(id) ON DELETE CASCADE)"
        )
        connection.execute(
            "CREATE INDEX IF NOT EXISTS idx_durable_job_events_job_id "
            "ON durable_job_events(job_id, id)"
        )
        connection.execute(
            "CREATE TABLE IF NOT EXISTS durable_job_meta ("
            "key TEXT PRIMARY KEY, value TEXT NOT NULL)"
        )
        connection.commit()
        return connection

    @staticmethod
    def _row_to_job(row: sqlite3.Row) -> DurableJob:
        return DurableJob(
            id=str(row["id"]),
            workspace_id=None if row["workspace_id"] is None else str(row["workspace_id"]),
            chat_id=None if row["chat_id"] is None else str(row["chat_id"]),
            goal=str(row["goal"]),
            status=str(row["status"]),
            checkpoint=str(row["checkpoint"]),
            result=None if row["result"] is None else str(row["result"]),
            error=None if row["error"] is None else str(row["error"]),
            cancel_requested=bool(row["cancel_requested"]),
            revision=int(row["revision"]),
            created_at=str(row["created_at"]),
            updated_at=str(row["updated_at"]),
        )

    @staticmethod
    def _row_to_event(row: sqlite3.Row) -> DurableJobEvent:
        return DurableJobEvent(
            id=int(row["id"]),
            job_id=str(row["job_id"]),
            from_status=None if row["from_status"] is None else str(row["from_status"]),
            to_status=str(row["to_status"]),
            note=str(row["note"]),
            created_at=str(row["created_at"]),
        )

    @staticmethod
    def _clean_optional(value: str | None) -> str | None:
        if value is None:
            return None
        cleaned = str(value).strip()
        return cleaned or None

    def create(
        self,
        goal: str,
        *,
        workspace_id: str | None = None,
        chat_id: str | None = None,
    ) -> DurableJob:
        goal = str(goal).strip()
        if not goal:
            raise ValueError("Job goal cannot be empty")
        if len(goal) > 8_000:
            raise ValueError("Job goal cannot exceed 8000 characters")
        workspace_id = self._clean_optional(workspace_id)
        chat_id = self._clean_optional(chat_id)
        now = _utc_now()
        job_id = uuid.uuid4().hex
        with self._connect() as connection:
            connection.execute(
                "INSERT INTO durable_jobs("
                "id, workspace_id, chat_id, goal, status, checkpoint, result, error, "
                "cancel_requested, revision, created_at, updated_at"
                ") VALUES (?, ?, ?, ?, 'queued', '', NULL, NULL, 0, 0, ?, ?)",
                (job_id, workspace_id, chat_id, goal, now, now),
            )
            self._append_event(connection, job_id, None, "queued", "Job created", now=now)
            row = connection.execute(
                "SELECT * FROM durable_jobs WHERE id = ?", (job_id,)
            ).fetchone()
        assert row is not None
        return self._row_to_job(row)

    def get(self, job_id: str) -> DurableJob:
        job_id = str(job_id).strip()
        if not job_id:
            raise ValueError("Job id is required")
        with self._connect() as connection:
            row = connection.execute(
                "SELECT * FROM durable_jobs WHERE id = ?", (job_id,)
            ).fetchone()
        if row is None:
            raise KeyError(f"Job does not exist: {job_id}")
        return self._row_to_job(row)

    def list(
        self,
        *,
        workspace_id: str | None,
        include_terminal: bool = True,
        limit: int = 50,
    ) -> list[DurableJob]:
        if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 200:
            raise ValueError("Job list limit must be between 1 and 200")
        workspace_id = self._clean_optional(workspace_id)
        clauses = ["workspace_id IS NULL"] if workspace_id is None else ["workspace_id = ?"]
        args: list[object] = [] if workspace_id is None else [workspace_id]
        if not include_terminal:
            clauses.append("status NOT IN ('completed', 'cancelled')")
        args.append(limit)
        with self._connect() as connection:
            rows = connection.execute(
                "SELECT * FROM durable_jobs WHERE "
                + " AND ".join(clauses)
                + " ORDER BY updated_at DESC, rowid DESC LIMIT ?",
                args,
            ).fetchall()
        return [self._row_to_job(row) for row in rows]

    def events(self, job_id: str, *, limit: int = 100) -> list[DurableJobEvent]:
        self.get(job_id)
        if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 500:
            raise ValueError("Job event limit must be between 1 and 500")
        with self._connect() as connection:
            rows = connection.execute(
                "SELECT * FROM durable_job_events WHERE job_id = ? "
                "ORDER BY id DESC LIMIT ?",
                (job_id, limit),
            ).fetchall()
        return [self._row_to_event(row) for row in reversed(rows)]

    def start(self, job_id: str) -> DurableJob:
        return self._transition(
            job_id,
            "running",
            allowed_from={"queued"},
            note="Job execution started",
            clear_error=True,
            clear_cancel_request=True,
        )

    def continue_after_permission(self, job_id: str) -> DurableJob:
        return self._transition(
            job_id,
            "running",
            allowed_from={"waiting_permission"},
            note="Permission approved; continuing once",
            clear_error=True,
        )

    def wait_for_permission(self, job_id: str, *, checkpoint: str) -> DurableJob:
        return self._transition(
            job_id,
            "waiting_permission",
            allowed_from={"running"},
            checkpoint=checkpoint,
            note="Waiting for one-shot permission",
        )

    def complete(self, job_id: str, *, result: str) -> DurableJob:
        return self._transition(
            job_id,
            "completed",
            allowed_from={"running"},
            result=result,
            note="Job completed",
            clear_error=True,
            clear_cancel_request=True,
        )

    def fail(self, job_id: str, *, error: str) -> DurableJob:
        return self._transition(
            job_id,
            "failed",
            allowed_from={"running", "waiting_permission"},
            error=str(error).strip()[:2_000] or "Job failed",
            note="Job failed",
        )

    def pause(
        self,
        job_id: str,
        *,
        checkpoint: str | None = None,
        note: str = "Job paused",
    ) -> DurableJob:
        return self._transition(
            job_id,
            "paused",
            allowed_from={"running", "waiting_permission"},
            checkpoint=checkpoint,
            note=note,
        )

    def resume(self, job_id: str) -> DurableJob:
        return self._transition(
            job_id,
            "queued",
            allowed_from={"paused", "failed"},
            note="Job queued for explicit resume",
            clear_error=True,
            clear_cancel_request=True,
        )

    def cancel(self, job_id: str) -> DurableJob:
        current = self.get(job_id)
        if current.status == "cancelled":
            return current
        if current.status == "completed":
            raise ValueError("Completed jobs cannot be cancelled")
        return self._transition(
            job_id,
            "cancelled",
            allowed_from={"queued", "running", "waiting_permission", "paused", "failed"},
            note="Job cancelled",
            clear_cancel_request=True,
        )

    def request_cancel(self, job_id: str) -> DurableJob:
        job = self.get(job_id)
        if job.status != "running":
            return self.cancel(job_id)
        now = _utc_now()
        with self._connect() as connection:
            cursor = connection.execute(
                "UPDATE durable_jobs SET cancel_requested = 1, revision = revision + 1, "
                "updated_at = ? WHERE id = ? AND status = 'running'",
                (now, job_id),
            )
            if cursor.rowcount != 1:
                raise ValueError("Job is no longer running")
            self._append_event(
                connection,
                job_id,
                "running",
                "running",
                "Cancellation requested",
                now=now,
            )
            row = connection.execute(
                "SELECT * FROM durable_jobs WHERE id = ?", (job_id,)
            ).fetchone()
        assert row is not None
        return self._row_to_job(row)

    def recover_interrupted(self) -> list[DurableJob]:
        """Pause in-flight work after a process restart instead of replaying it."""

        recovered_ids: list[str] = []
        with self._connect() as connection:
            rows = connection.execute(
                "SELECT id, checkpoint FROM durable_jobs WHERE status = 'running'"
            ).fetchall()
            for row in rows:
                job_id = str(row["id"])
                checkpoint = str(row["checkpoint"])
                recovery_note = "Interrupted before clean completion; resume explicitly."
                checkpoint = recovery_note if not checkpoint else checkpoint.rstrip() + "\n\n" + recovery_note
                now = _utc_now()
                cursor = connection.execute(
                    "UPDATE durable_jobs SET status = 'paused', checkpoint = ?, "
                    "cancel_requested = 0, revision = revision + 1, updated_at = ? "
                    "WHERE id = ? AND status = 'running'",
                    (checkpoint, now, job_id),
                )
                if cursor.rowcount == 1:
                    recovered_ids.append(job_id)
                    self._append_event(
                        connection,
                        job_id,
                        "running",
                        "paused",
                        "Recovered interrupted job without replaying work",
                        now=now,
                    )
            if not recovered_ids:
                return []
            placeholders = ",".join("?" for _ in recovered_ids)
            updated = connection.execute(
                f"SELECT * FROM durable_jobs WHERE id IN ({placeholders})",
                recovered_ids,
            ).fetchall()
        return [self._row_to_job(row) for row in updated]

    def set_pending_approval_owner(self, job_id: str) -> None:
        job = self.get(job_id)
        if job.status != "waiting_permission":
            raise ValueError("Only a waiting job can own a pending approval")
        with self._connect() as connection:
            connection.execute(
                "INSERT OR REPLACE INTO durable_job_meta(key, value) VALUES "
                "('pending_approval_job_id', ?)",
                (job.id,),
            )

    def pending_approval_owner(self) -> str | None:
        with self._connect() as connection:
            row = connection.execute(
                "SELECT value FROM durable_job_meta WHERE key = 'pending_approval_job_id'"
            ).fetchone()
        return None if row is None else str(row["value"])

    def clear_pending_approval_owner(self) -> None:
        with self._connect() as connection:
            connection.execute(
                "DELETE FROM durable_job_meta WHERE key = 'pending_approval_job_id'"
            )

    def _transition(
        self,
        job_id: str,
        to_status: str,
        *,
        allowed_from: set[str],
        checkpoint: str | None = None,
        result: str | None = None,
        error: str | None = None,
        note: str,
        clear_error: bool = False,
        clear_cancel_request: bool = False,
    ) -> DurableJob:
        if to_status not in JOB_STATUSES:
            raise ValueError(f"Unsupported job status: {to_status}")
        job_id = str(job_id).strip()
        now = _utc_now()
        with self._connect() as connection:
            row = connection.execute(
                "SELECT * FROM durable_jobs WHERE id = ?", (job_id,)
            ).fetchone()
            if row is None:
                raise KeyError(f"Job does not exist: {job_id}")
            current = self._row_to_job(row)
            if current.status not in allowed_from:
                raise ValueError(
                    f"Job {job_id} cannot transition from {current.status} to {to_status}"
                )

            next_checkpoint = current.checkpoint if checkpoint is None else str(checkpoint).strip()
            next_result = current.result if result is None else str(result)
            next_error = None if clear_error else current.error
            if error is not None:
                next_error = str(error).strip() or None
            next_cancel = False if clear_cancel_request else current.cancel_requested

            connection.execute(
                "UPDATE durable_jobs SET status = ?, checkpoint = ?, result = ?, error = ?, "
                "cancel_requested = ?, revision = revision + 1, updated_at = ? WHERE id = ?",
                (
                    to_status,
                    next_checkpoint,
                    next_result,
                    next_error,
                    1 if next_cancel else 0,
                    now,
                    job_id,
                ),
            )
            self._append_event(
                connection,
                job_id,
                current.status,
                to_status,
                note,
                now=now,
            )
            updated = connection.execute(
                "SELECT * FROM durable_jobs WHERE id = ?", (job_id,)
            ).fetchone()
        assert updated is not None
        return self._row_to_job(updated)

    @staticmethod
    def _append_event(
        connection: sqlite3.Connection,
        job_id: str,
        from_status: str | None,
        to_status: str,
        note: str,
        *,
        now: str,
    ) -> None:
        connection.execute(
            "INSERT INTO durable_job_events("
            "job_id, from_status, to_status, note, created_at"
            ") VALUES (?, ?, ?, ?, ?)",
            (job_id, from_status, to_status, str(note).strip(), now),
        )
