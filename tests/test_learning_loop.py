from babyai.autodidact import LessonCandidate
from babyai.curiosa import CuriosityQuestion
from babyai.evidence import EvidenceAssessment, EvidenceItem, EvidenceState, EvidenceVerdict
from babyai.hypothesis import HypothesisRecord
from babyai.learning_loop import LearningLoop
from babyai.working_memory import TaskState


def test_loop_starts_with_task() -> None:
    snapshot = LearningLoop.evaluate(None, None, EvidenceState(), None, None)
    assert "task set" in snapshot.next_step


def test_loop_requests_hypothesis_after_task() -> None:
    snapshot = LearningLoop.evaluate(TaskState(goal="Investigate"), None, EvidenceState(), None, None)
    assert "hypothesis propose" in snapshot.next_step


def test_loop_requests_evidence_then_assessment() -> None:
    hypothesis = HypothesisRecord(claim="A", expected_result="B", test="C")
    task = TaskState(goal="Investigate")
    snapshot = LearningLoop.evaluate(task, hypothesis, EvidenceState(), None, None)
    assert "evidence add" in snapshot.next_step

    snapshot = LearningLoop.evaluate(
        task,
        hypothesis,
        EvidenceState(items=[EvidenceItem("Observed")]),
        None,
        None,
    )
    assert "evidence assess" in snapshot.next_step


def test_inconclusive_evidence_routes_to_curiosa() -> None:
    snapshot = LearningLoop.evaluate(
        TaskState(goal="Investigate"),
        HypothesisRecord(claim="A", expected_result="B", test="C"),
        EvidenceState(
            items=[EvidenceItem("Observed")],
            assessment=EvidenceAssessment(EvidenceVerdict.INCONCLUSIVE, "Need more data"),
        ),
        None,
        None,
    )
    assert "curiosa propose" in snapshot.next_step


def test_confirmed_hypothesis_routes_to_lesson_then_review() -> None:
    task = TaskState(goal="Investigate")
    hypothesis = HypothesisRecord(claim="A", expected_result="B", test="C", status="confirmed")
    evidence = EvidenceState(
        items=[EvidenceItem("Observed")],
        assessment=EvidenceAssessment(EvidenceVerdict.SUPPORTS, "Matches expectation"),
    )
    snapshot = LearningLoop.evaluate(task, hypothesis, evidence, CuriosityQuestion("Q?", "R"), None)
    assert "learn propose" in snapshot.next_step

    snapshot = LearningLoop.evaluate(
        task,
        hypothesis,
        evidence,
        None,
        LessonCandidate("K", "R", "S"),
    )
    assert "approve or reject" in snapshot.next_step
