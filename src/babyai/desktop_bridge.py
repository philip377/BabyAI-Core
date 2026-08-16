from __future__ import annotations

import json
from dataclasses import asdict, dataclass

from .autodidact import LessonCandidateStore
from .brain import probe_brain_runtime
from .config import BabyAIConfig
from .curiosa import CuriosityStore
from .evidence import EvidenceStore
from .hypothesis import HypothesisStore
from .identity import Identity
from .learning_loop import LearningLoop
from .permissions import Capability, PermissionStore
from .tool_approval import PendingToolApprovalStore
from .working_memory import WorkingMemoryStore


@dataclass(slots=True)
class DesktopSnapshot:
    identity: dict[str, str]
    task: dict[str, str] | None
    learning: dict[str, object]
    permissions: dict[str, bool]
    runtime: dict[str, object]

    def as_dict(self) -> dict[str, object]:
        return {
            "schema_version": 1,
            "identity": self.identity,
            "task": self.task,
            "learning": self.learning,
            "permissions": self.permissions,
            "runtime": self.runtime,
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
    tool_approval = PendingToolApprovalStore(config.pending_tool_approval_file).load()
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
        "tool_approval": None if tool_approval is None else {
            "tool": tool_approval.tool,
            "arguments": tool_approval.arguments,
            "capability": tool_approval.capability,
        },
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
        runtime=probe_brain_runtime(config).as_dict(),
    )
