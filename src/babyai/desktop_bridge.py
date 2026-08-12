from __future__ import annotations

import json
from dataclasses import asdict, dataclass

from .autodidact import LessonCandidateStore
from .config import BabyAIConfig
from .curiosa import CuriosityStore
from .evidence import EvidenceStore
from .hypothesis import HypothesisStore
from .identity import Identity
from .learning_loop import LearningLoop
from .permissions import Capability, PermissionStore
from .working_memory import WorkingMemoryStore


@dataclass(slots=True)
class DesktopSnapshot:
    identity: dict[str, str]
    task: dict[str, str] | None
    learning: dict[str, object]
    permissions: dict[str, bool]

    def as_dict(self) -> dict[str, object]:
        return {
            "schema_version": 1,
            "identity": self.identity,
            "task": self.task,
            "learning": self.learning,
            "permissions": self.permissions,
        }


def _read_identity(config: BabyAIConfig) -> Identity:
    if not config.identity_file.exists():
        return Identity(name=config.name, owner=config.owner)
    data = json.loads(config.identity_file.read_text(encoding="utf-8"))
    return Identity(**data)


def build_desktop_snapshot(config: BabyAIConfig | None = None) -> DesktopSnapshot:
    config = config or BabyAIConfig.default()

    identity = _read_identity(config)
    task = WorkingMemoryStore(config.working_memory_file).load()
    hypothesis = HypothesisStore(config.hypothesis_file).load()
    evidence = EvidenceStore(config.evidence_file).load()
    curiosity = CuriosityStore(config.curiosity_file).load()
    lesson = LessonCandidateStore(config.lesson_candidate_file).load()
    loop = LearningLoop.evaluate(task, hypothesis, evidence, curiosity, lesson)

    permissions_store = PermissionStore(config.permissions_file)
    permissions = {
        capability.value: permissions_store.is_granted(capability)
        for capability in Capability
    }

    learning = {
        "hypothesis": None if hypothesis is None else {
            "claim": hypothesis.claim,
            "expected_result": hypothesis.expected_result,
            "test": hypothesis.test,
            "status": hypothesis.status,
        },
        "evidence_count": len(evidence.items),
        "evidence_verdict": None if evidence.assessment is None else evidence.assessment.verdict.value,
        "curiosity": None if curiosity is None else asdict(curiosity),
        "lesson": None if lesson is None else asdict(lesson),
        "next_step": loop.next_step,
    }

    return DesktopSnapshot(
        identity={
            "name": identity.name,
            "owner": identity.owner,
            "version": identity.version,
            "purpose": identity.purpose,
        },
        task=None if task is None else asdict(task),
        learning=learning,
        permissions=permissions,
    )
