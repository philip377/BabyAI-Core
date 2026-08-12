from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from pathlib import Path

from .llm import LLMProvider


class CuriosaProtocolError(ValueError):
    pass


@dataclass(slots=True)
class CuriosityQuestion:
    question: str
    reason: str

    def as_context(self) -> str:
        return f"Question: {self.question}\nReason: {self.reason}"


class Curiosa:
    def __init__(self, llm: LLMProvider) -> None:
        self.llm = llm

    def propose(self, context: str) -> CuriosityQuestion:
        text = context.strip()
        if not text:
            raise ValueError("Context cannot be empty")
        prompt = (
            "Identify exactly one missing piece of information that would most reduce uncertainty. "
            "Return ONLY JSON with exactly keys question and reason. "
            "Do not search, call tools, answer the question, or write memory.\n"
            f"Context:\n{text}\n"
        )
        return self.parse(self.llm.generate(prompt))

    @staticmethod
    def parse(text: str) -> CuriosityQuestion:
        try:
            data = json.loads(text.strip())
        except json.JSONDecodeError as exc:
            raise CuriosaProtocolError("CURIOSA response must be valid JSON") from exc
        if not isinstance(data, dict) or set(data) != {"question", "reason"}:
            raise CuriosaProtocolError("CURIOSA schema must contain exactly question and reason")
        question = data.get("question")
        reason = data.get("reason")
        if not isinstance(question, str) or not question.strip():
            raise CuriosaProtocolError("question must be a non-empty string")
        if not isinstance(reason, str) or not reason.strip():
            raise CuriosaProtocolError("reason must be a non-empty string")
        return CuriosityQuestion(question=question.strip(), reason=reason.strip())


class CuriosityStore:
    def __init__(self, path: Path) -> None:
        self.path = path

    def save(self, item: CuriosityQuestion) -> CuriosityQuestion:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(json.dumps(asdict(item), ensure_ascii=False, indent=2), encoding="utf-8")
        return item

    def load(self) -> CuriosityQuestion | None:
        if not self.path.exists():
            return None
        data = json.loads(self.path.read_text(encoding="utf-8"))
        return CuriosityQuestion(question=str(data["question"]), reason=str(data["reason"]))

    def clear(self) -> None:
        if self.path.exists():
            self.path.unlink()
