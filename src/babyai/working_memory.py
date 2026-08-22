from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from pathlib import Path


@dataclass(slots=True)
class TaskState:
    goal: str
    status: str = "active"
    context: str = ""
    project: str = ""

    def as_context(self) -> str:
        parts = [f"Goal: {self.goal}", f"Status: {self.status}"]
        if self.context:
            parts.append(f"Context: {self.context}")
        if self.project:
            parts.append(f"Project: {self.project}")
        return "Current task:\n" + "\n".join(parts)


class WorkingMemoryStore:
    def __init__(self, path: Path) -> None:
        self.path = path

    def load(self) -> TaskState | None:
        if not self.path.exists():
            return None
        data = json.loads(self.path.read_text(encoding="utf-8"))
        return TaskState(
            goal=str(data.get("goal", "")).strip(),
            status=str(data.get("status", "active")).strip() or "active",
            context=str(data.get("context", "")).strip(),
            project=str(data.get("project", "")).strip(),
        )

    def save(self, task: TaskState) -> TaskState:
        if not task.goal.strip():
            raise ValueError("Task goal cannot be empty")
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(
            json.dumps(asdict(task), ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        return task

    def clear(self) -> None:
        if self.path.exists():
            self.path.unlink()
