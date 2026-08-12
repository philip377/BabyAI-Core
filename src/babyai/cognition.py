from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from pathlib import Path

from .llm import LLMProvider
from .working_memory import TaskState


class CognitionProtocolError(ValueError):
    pass


@dataclass(frozen=True, slots=True)
class TaskProposal:
    goal: str
    status: str
    context: str

    def as_context(self) -> str:
        return (
            "Proposed task update:\n"
            f"Goal: {self.goal}\n"
            f"Status: {self.status}\n"
            f"Context: {self.context}"
        )


class Cognition:
    def __init__(self, llm: LLMProvider) -> None:
        self.llm = llm

    def propose(self, task: TaskState, observation: str) -> TaskProposal:
        prompt = (
            "You update task state conservatively. Return ONLY JSON with exactly these keys: "
            '"goal", "status", "context". Keep goal stable unless the user clearly changed it. '
            "Do not include reasoning.\n\n"
            + task.as_context()
            + f"\n\nNew observation:\n{observation}"
        )
        raw = self.llm.generate(prompt).strip()
        try:
            data = json.loads(raw)
        except json.JSONDecodeError as exc:
            raise CognitionProtocolError("Invalid cognition JSON") from exc
        if not isinstance(data, dict) or set(data) != {"goal", "status", "context"}:
            raise CognitionProtocolError("Invalid cognition schema")
        goal = str(data["goal"]).strip()
        status = str(data["status"]).strip()
        context = str(data["context"]).strip()
        if not goal or not status:
            raise CognitionProtocolError("Goal and status cannot be empty")
        return TaskProposal(goal=goal, status=status, context=context)


class TaskProposalStore:
    def __init__(self, path: Path) -> None:
        self.path = path

    def save(self, proposal: TaskProposal) -> TaskProposal:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(
            json.dumps(asdict(proposal), ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        return proposal

    def load(self) -> TaskProposal | None:
        if not self.path.exists():
            return None
        data = json.loads(self.path.read_text(encoding="utf-8"))
        return TaskProposal(
            goal=str(data.get("goal", "")).strip(),
            status=str(data.get("status", "")).strip(),
            context=str(data.get("context", "")).strip(),
        )

    def clear(self) -> None:
        if self.path.exists():
            self.path.unlink()
