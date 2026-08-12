from __future__ import annotations

from dataclasses import dataclass

from .autodidact import LessonCandidate
from .curiosa import CuriosityQuestion
from .evidence import EvidenceState
from .hypothesis import HypothesisRecord
from .working_memory import TaskState


@dataclass(slots=True)
class LearningLoopSnapshot:
    task: TaskState | None
    hypothesis: HypothesisRecord | None
    evidence: EvidenceState
    curiosity: CuriosityQuestion | None
    lesson: LessonCandidate | None
    next_step: str

    def as_context(self) -> str:
        task_state = "present" if self.task else "missing"
        hypothesis_state = self.hypothesis.status if self.hypothesis else "missing"
        evidence_state = (
            self.evidence.assessment.verdict.value
            if self.evidence.assessment is not None
            else ("collected" if self.evidence.items else "missing")
        )
        curiosity_state = "present" if self.curiosity else "missing"
        lesson_state = "pending" if self.lesson else "missing"
        return (
            "Learning loop:\n"
            f"task={task_state}\n"
            f"hypothesis={hypothesis_state}\n"
            f"evidence={evidence_state}\n"
            f"curiosity={curiosity_state}\n"
            f"lesson={lesson_state}\n"
            f"next_step={self.next_step}"
        )


class LearningLoop:
    """Read-only coordinator for BabyAI's explicit learning states."""

    @staticmethod
    def evaluate(
        task: TaskState | None,
        hypothesis: HypothesisRecord | None,
        evidence: EvidenceState,
        curiosity: CuriosityQuestion | None,
        lesson: LessonCandidate | None,
    ) -> LearningLoopSnapshot:
        if task is None:
            next_step = "Set an active task with 'babyai task set'."
        elif hypothesis is None:
            next_step = "Propose one testable hypothesis with 'babyai hypothesis propose'."
        elif not evidence.items:
            next_step = "Add an explicit observation with 'babyai-evidence add'."
        elif evidence.assessment is None:
            next_step = "Assess the collected evidence with 'babyai-evidence assess'."
        elif evidence.assessment.verdict.value == "inconclusive" and curiosity is None:
            next_step = "Ask CURIOSA for the single most useful missing fact with 'babyai-curiosa propose'."
        elif hypothesis.status == "pending":
            next_step = "Review the evidence and explicitly confirm or reject the hypothesis."
        elif lesson is None:
            next_step = "Propose one durable lesson with 'babyai-learn propose'."
        else:
            next_step = "Review the lesson candidate and explicitly approve or reject it."

        return LearningLoopSnapshot(
            task=task,
            hypothesis=hypothesis,
            evidence=evidence,
            curiosity=curiosity,
            lesson=lesson,
            next_step=next_step,
        )
