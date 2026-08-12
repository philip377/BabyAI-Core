from __future__ import annotations

import json
from dataclasses import asdict, dataclass, field
from enum import Enum
from pathlib import Path

from .hypothesis import HypothesisRecord
from .llm import LLMProvider


class EvidenceVerdict(str, Enum):
    SUPPORTS = "supports"
    CONTRADICTS = "contradicts"
    INCONCLUSIVE = "inconclusive"


class EvidenceProtocolError(ValueError):
    pass


@dataclass(slots=True)
class EvidenceItem:
    observation: str


@dataclass(slots=True)
class EvidenceAssessment:
    verdict: EvidenceVerdict
    summary: str

    def as_context(self) -> str:
        return f"Verdict: {self.verdict.value}\nSummary: {self.summary}"


@dataclass(slots=True)
class EvidenceState:
    items: list[EvidenceItem] = field(default_factory=list)
    assessment: EvidenceAssessment | None = None


class EvidenceEvaluator:
    def __init__(self, llm: LLMProvider) -> None:
        self.llm = llm

    def assess(self, hypothesis: HypothesisRecord, observations: list[EvidenceItem]) -> EvidenceAssessment:
        if not observations:
            raise ValueError("At least one evidence observation is required")
        evidence_text = "\n".join(f"- {item.observation}" for item in observations)
        prompt = (
            "Evaluate explicit evidence against the hypothesis. "
            "Return ONLY JSON with keys verdict and summary. "
            "verdict must be exactly supports, contradicts, or inconclusive. "
            "Do not execute tests, call tools, or change hypothesis state.\n"
            f"Hypothesis claim: {hypothesis.claim}\n"
            f"Expected result: {hypothesis.expected_result}\n"
            f"Evidence:\n{evidence_text}\n"
        )
        return self.parse(self.llm.generate(prompt))

    @staticmethod
    def parse(text: str) -> EvidenceAssessment:
        try:
            data = json.loads(text.strip())
        except json.JSONDecodeError as exc:
            raise EvidenceProtocolError("Evidence assessment must be valid JSON") from exc
        if not isinstance(data, dict) or set(data) != {"verdict", "summary"}:
            raise EvidenceProtocolError("Evidence schema must contain exactly verdict and summary")
        verdict_raw = data.get("verdict")
        summary = data.get("summary")
        if verdict_raw not in {item.value for item in EvidenceVerdict}:
            raise EvidenceProtocolError("Invalid evidence verdict")
        if not isinstance(summary, str) or not summary.strip():
            raise EvidenceProtocolError("Evidence summary must be a non-empty string")
        return EvidenceAssessment(verdict=EvidenceVerdict(verdict_raw), summary=summary.strip())


class EvidenceStore:
    def __init__(self, path: Path) -> None:
        self.path = path

    def load(self) -> EvidenceState:
        if not self.path.exists():
            return EvidenceState()
        data = json.loads(self.path.read_text(encoding="utf-8"))
        items = [EvidenceItem(observation=str(item.get("observation", "")).strip()) for item in data.get("items", [])]
        assessment_data = data.get("assessment")
        assessment = None
        if isinstance(assessment_data, dict):
            assessment = EvidenceAssessment(
                verdict=EvidenceVerdict(str(assessment_data.get("verdict"))),
                summary=str(assessment_data.get("summary", "")).strip(),
            )
        return EvidenceState(items=items, assessment=assessment)

    def save(self, state: EvidenceState) -> EvidenceState:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "items": [asdict(item) for item in state.items],
            "assessment": None if state.assessment is None else {
                "verdict": state.assessment.verdict.value,
                "summary": state.assessment.summary,
            },
        }
        self.path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
        return state

    def add(self, observation: str) -> EvidenceState:
        text = observation.strip()
        if not text:
            raise ValueError("Evidence observation cannot be empty")
        state = self.load()
        state.items.append(EvidenceItem(observation=text))
        state.assessment = None
        return self.save(state)

    def set_assessment(self, assessment: EvidenceAssessment) -> EvidenceState:
        state = self.load()
        state.assessment = assessment
        return self.save(state)

    def clear(self) -> None:
        if self.path.exists():
            self.path.unlink()
