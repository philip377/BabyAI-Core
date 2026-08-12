from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from pathlib import Path

from .llm import LLMProvider


class AutodidactProtocolError(ValueError):
    pass


@dataclass(slots=True)
class LessonCandidate:
    knowledge: str
    rationale: str
    source: str
    status: str = "pending"

    def as_context(self) -> str:
        return (
            "Lesson candidate:\n"
            f"Knowledge: {self.knowledge}\n"
            f"Rationale: {self.rationale}\n"
            f"Source: {self.source}\n"
            f"Status: {self.status}"
        )


class Autodidact:
    def __init__(self, llm: LLMProvider) -> None:
        self.llm = llm

    def propose(self, context: str) -> LessonCandidate:
        if not context.strip():
            raise ValueError("Learning context cannot be empty")
        prompt = (
            "Extract exactly one durable lesson worth remembering from the supplied context. "
            "Return ONLY JSON with keys knowledge, rationale, source. "
            "The lesson must be concise, general enough to reuse, and directly supported by the context. "
            "Do not invent facts, execute tools, change permissions, or store anything.\n"
            f"Context:\n{context.strip()}\n"
        )
        return self.parse(self.llm.generate(prompt))

    @staticmethod
    def parse(text: str) -> LessonCandidate:
        try:
            data = json.loads(text.strip())
        except json.JSONDecodeError as exc:
            raise AutodidactProtocolError("Lesson candidate must be valid JSON") from exc
        if not isinstance(data, dict) or set(data) != {"knowledge", "rationale", "source"}:
            raise AutodidactProtocolError(
                "Lesson schema must contain exactly knowledge, rationale, and source"
            )
        values = {key: data[key] for key in ("knowledge", "rationale", "source")}
        if not all(isinstance(value, str) and value.strip() for value in values.values()):
            raise AutodidactProtocolError("Lesson fields must be non-empty strings")
        return LessonCandidate(
            knowledge=values["knowledge"].strip(),
            rationale=values["rationale"].strip(),
            source=values["source"].strip(),
        )


class LessonCandidateStore:
    def __init__(self, path: Path) -> None:
        self.path = path

    def save(self, candidate: LessonCandidate) -> LessonCandidate:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(
            json.dumps(asdict(candidate), ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        return candidate

    def load(self) -> LessonCandidate | None:
        if not self.path.exists():
            return None
        data = json.loads(self.path.read_text(encoding="utf-8"))
        return LessonCandidate(
            knowledge=str(data.get("knowledge", "")).strip(),
            rationale=str(data.get("rationale", "")).strip(),
            source=str(data.get("source", "")).strip(),
            status=str(data.get("status", "pending")).strip() or "pending",
        )

    def clear(self) -> None:
        if self.path.exists():
            self.path.unlink()
