from babyai.autodidact import LessonCandidate, LessonCandidateStore
from babyai.config import BabyAIConfig
from babyai.curiosa import CuriosityQuestion, CuriosityStore
from babyai.evidence import EvidenceAssessment, EvidenceItem, EvidenceState, EvidenceStore, EvidenceVerdict
from babyai.hypothesis import HypothesisRecord, HypothesisStore
from babyai.learning_loop import LearningLoop
from babyai.memory import MemoryKind, SQLiteMemoryStore
from babyai.working_memory import TaskState, WorkingMemoryStore


def test_cognitive_learning_loop_reaches_durable_knowledge(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    config = BabyAIConfig.default()

    task_store = WorkingMemoryStore(config.working_memory_file)
    hypothesis_store = HypothesisStore(config.hypothesis_file)
    evidence_store = EvidenceStore(config.evidence_file)
    curiosity_store = CuriosityStore(config.curiosity_file)
    lesson_store = LessonCandidateStore(config.lesson_candidate_file)
    memory = SQLiteMemoryStore(config.memory_db)

    task = task_store.save(TaskState(goal="Learn why the service failed"))
    snapshot = LearningLoop.evaluate(task, None, evidence_store.load(), None, None)
    assert "hypothesis" in snapshot.next_step.lower()

    hypothesis = hypothesis_store.save(
        HypothesisRecord(
            claim="The failure was caused by upstream unavailability",
            expected_result="Requests fail while upstream is unavailable",
            test="Compare failures with upstream health observations",
        )
    )
    snapshot = LearningLoop.evaluate(task, hypothesis, evidence_store.load(), None, None)
    assert "observation" in snapshot.next_step.lower()

    evidence_store.add("Requests returned HTTP 503 while upstream health was red")
    snapshot = LearningLoop.evaluate(task, hypothesis, evidence_store.load(), None, None)
    assert "assess" in snapshot.next_step.lower()

    evidence_store.set_assessment(
        EvidenceAssessment(
            verdict=EvidenceVerdict.INCONCLUSIVE,
            summary="The observation is suggestive but not sufficient by itself",
        )
    )
    snapshot = LearningLoop.evaluate(task, hypothesis, evidence_store.load(), None, None)
    assert "curiosa" in snapshot.next_step.lower()

    curiosity = curiosity_store.save(
        CuriosityQuestion(
            question="Did failures stop when upstream health recovered?",
            reason="That would distinguish correlation from a likely causal dependency",
        )
    )
    snapshot = LearningLoop.evaluate(task, hypothesis, evidence_store.load(), curiosity, None)
    assert "confirm or reject" in snapshot.next_step.lower()

    hypothesis = hypothesis_store.set_status("confirmed")
    snapshot = LearningLoop.evaluate(task, hypothesis, evidence_store.load(), curiosity, None)
    assert "lesson" in snapshot.next_step.lower()

    lesson = lesson_store.save(
        LessonCandidate(
            knowledge="When HTTP 503 failures align with upstream outages and recoveries, check the upstream dependency first.",
            rationale="The confirmed hypothesis connected service failures to upstream availability.",
            source="explicit task, hypothesis, and evidence",
        )
    )
    snapshot = LearningLoop.evaluate(task, hypothesis, evidence_store.load(), curiosity, lesson)
    assert "approve or reject" in snapshot.next_step.lower()

    before = memory.recent(kind=MemoryKind.KNOWLEDGE)
    assert before == []

    record = memory.add("autodidact", lesson.knowledge, kind=MemoryKind.KNOWLEDGE)
    lesson_store.clear()

    learned = memory.recent(kind=MemoryKind.KNOWLEDGE)
    assert len(learned) == 1
    assert learned[0].id == record.id
    assert learned[0].content == lesson.knowledge
    assert lesson_store.load() is None


def test_learning_loop_is_read_only(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    config = BabyAIConfig.default()
    task_store = WorkingMemoryStore(config.working_memory_file)
    evidence_store = EvidenceStore(config.evidence_file)
    memory = SQLiteMemoryStore(config.memory_db)

    task = task_store.save(TaskState(goal="Inspect a problem"))
    hypothesis = HypothesisRecord(claim="A", expected_result="B", test="C")
    evidence = EvidenceState(items=[EvidenceItem(observation="Observed D")])

    before_memory = memory.recent()
    before_task = task_store.load()

    snapshot = LearningLoop.evaluate(task, hypothesis, evidence, None, None)

    assert snapshot.task is task
    assert memory.recent() == before_memory
    assert task_store.load() == before_task
    assert not config.curiosity_file.exists()
    assert not config.lesson_candidate_file.exists()
