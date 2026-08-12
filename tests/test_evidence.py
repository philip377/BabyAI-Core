import pytest

from babyai.evidence import (
    EvidenceAssessment,
    EvidenceEvaluator,
    EvidenceItem,
    EvidenceProtocolError,
    EvidenceStore,
    EvidenceVerdict,
)
from babyai.hypothesis import HypothesisRecord


class StubLLM:
    def __init__(self, response: str) -> None:
        self.response = response
        self.prompts: list[str] = []

    def generate(self, prompt: str) -> str:
        self.prompts.append(prompt)
        return self.response


def hypothesis() -> HypothesisRecord:
    return HypothesisRecord(
        claim="The cache is stale",
        expected_result="Refreshing the cache changes the value",
        test="Compare the value before and after a refresh",
    )


def test_evaluator_accepts_strict_verdict() -> None:
    llm = StubLLM('{"verdict":"supports","summary":"The observation matches the expected result."}')
    result = EvidenceEvaluator(llm).assess(hypothesis(), [EvidenceItem("Value changed after refresh")])
    assert result.verdict is EvidenceVerdict.SUPPORTS
    assert "matches" in result.summary
    assert "Do not execute tests" in llm.prompts[0]


def test_evaluator_rejects_extra_fields() -> None:
    with pytest.raises(EvidenceProtocolError):
        EvidenceEvaluator.parse('{"verdict":"supports","summary":"ok","reasoning":"hidden"}')


def test_store_keeps_evidence_separate_from_hypothesis(tmp_path) -> None:
    evidence_path = tmp_path / "evidence.json"
    hypothesis_path = tmp_path / "hypothesis.json"
    hypothesis_path.write_text('{"claim":"x","expected_result":"y","test":"z","status":"pending"}')

    store = EvidenceStore(evidence_path)
    state = store.add("Observed y")
    assert len(state.items) == 1
    assert hypothesis_path.read_text().find('"status":"pending"') != -1


def test_adding_observation_invalidates_old_assessment(tmp_path) -> None:
    store = EvidenceStore(tmp_path / "evidence.json")
    store.add("first")
    store.set_assessment(EvidenceAssessment(EvidenceVerdict.SUPPORTS, "initial"))
    state = store.add("second")
    assert state.assessment is None


def test_assessment_does_not_change_hypothesis_status(tmp_path) -> None:
    from babyai.hypothesis import HypothesisStore

    hstore = HypothesisStore(tmp_path / "hypothesis.json")
    hstore.save(hypothesis())
    estore = EvidenceStore(tmp_path / "evidence.json")
    estore.add("Value changed")
    estore.set_assessment(EvidenceAssessment(EvidenceVerdict.SUPPORTS, "supports claim"))

    assert hstore.load().status == "pending"
