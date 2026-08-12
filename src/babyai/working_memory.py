from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from enum import StrEnum
from pathlib import Path


class TaskStatus(StrEnum):
    ACTIVE = "active"
    PAUSED = "paused"
    DONE = "done"


@dataclass(slots=True)
class TaskState:
    goal: str
    status: TaskStatus = TaskStatus.ACTIVE
    summary: str = ""

    def as_context(self) -> str:
        parts = [f"Goal: {self.goal}", f"Status: {self.status.value}"]
        if self.summary:
            parts.append(f"Working summary: {self.summary}")
        return "\n".join(parts)


@dataclass(slots=True)
class WorkingMemoryStore:
    path: Path

    def load(self) -> TaskState | None:
        if not self.path.exists():
            return None
        data = json.loads(self.path.read_text(encoding="utf-8"))
        if not isinstance(data, dict):
            raise ValueError("Invalid working-memory state")
        goal = data.get("goal")
        status = data.get("status", TaskStatus.ACTIVE.value)
        summary = data.get("summary", "")
        if not isinstance(goal, str) or not goal.strip():
            raise ValueError("Working-memory goal must be non-empty")
        if not isinstance(summary, str):
            raise ValueError("Working-memory summary must be text")
        return TaskState(goal=goal, status=TaskStatus(status), summary=summary)

    def save(self, state: TaskState) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        payload = asdict(state)
        payload["status"] = state.status.value
        tmp = self.path.with_suffix(self.path.suffix + ".tmp")
        tmp.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
        tmp.replace(self.path)

    def clear(self) -> None:
        if self.path.exists():
            self.path.unlink()
