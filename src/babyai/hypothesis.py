from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from pathlib import Path

from .llm import LLMProvider


class HypothesisProtocolError(ValueError):
    pass


@dataclass(slots=True)
class HypothesisRecord:
    claim: str
    expected_result: str
    test: str
    status: str = "pending"

    def as_context(self) -> str:
        return (
            "Hypothesis:\n"
            f"Claim: {self.claim}\n"
            f"Expected result: {self.expected_result}\n"
            f"Test: {self.test}\n"
            f"Status: {self.status}"
        )


class Hypothesis:
    def __init__(self, llm: LLMProvider) -> None:
        self.llm = llm

    def propose(self, question: str, context: str = "") -> HypothesisRecord:
        prompt = (
            "Create exactly one concise, testable hypothesis. "
            "Return ONLY JSON with keys claim, expected_result, test. "
            "Do not include commands to execute, permissions, hidden reasoning, or extra fields.\n"
            f"Question: {question}\n"
        )
        if context.strip():
            prompt += f"Context: {context.strip()}\n"
        raw = self.llm.generate(prompt)
        return self.parse(raw)

    @staticmethod
    def parse(text: str) -> HypothesisRecord:
        try:
            data = json.loads(text.strip())
        except json.JSONDecodeError as exc:
            raise HypothesisProtocolError("Hypothesis output must be valid JSON") from exc
        if not isinstance(data, dict):
            raise HypothesisProtocolError("Hypothesis output must be a JSON object")
        expected = {"claim", "expected_result", "test"}
        if set(data) != expected:
            raise HypothesisProtocolError("Hypothesis schema must contain exactly claim, expected_result, and test")
        values = {key: data[key] for key in expected}
        if not all(isinstance(value, str) and value.strip() for value in values.values()):
            raise HypothesisProtocolError("Hypothesis fields must be non-empty strings")
        return HypothesisRecord(
            claim=values["claim"].strip(),
            expected_result=values["expected_result"].strip(),
            test=values["test"].strip(),
        )


class HypothesisStore:
    def __init__(self, path: Path) -> None:
        self.path = path

    def save(self, record: HypothesisRecord) -> HypothesisRecord:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(json.dumps(asdict(record), ensure_ascii=False, indent=2), encoding="utf-8")
        return record

    def load(self) -> HypothesisRecord | None:
        if not self.path.exists():
            return None
        data = json.loads(self.path.read_text(encoding="utf-8"))
        return HypothesisRecord(
            claim=str(data.get("claim", "")).strip(),
            expected_result=str(data.get("expected_result", "")).strip(),
            test=str(data.get("test", "")).strip(),
            status=str(data.get("status", "pending")).strip() or "pending",
        )

    def set_status(self, status: str) -> HypothesisRecord:
        record = self.load()
        if record is None:
            raise ValueError("No stored hypothesis")
        record.status = status.strip() or record.status
        return self.save(record)

    def clear(self) -> None:
        if self.path.exists():
            self.path.unlink()
