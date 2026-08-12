from __future__ import annotations

import json
from dataclasses import dataclass
from enum import StrEnum


class PlanAction(StrEnum):
    ANSWER = "answer"
    TOOL = "tool"


@dataclass(frozen=True, slots=True)
class Plan:
    intent: str
    action: PlanAction


class PlannerProtocolError(ValueError):
    pass


class Planner:
    """Parse one-step plans produced by the model.

    Planner v1 is deliberately small: it only captures a short intent and
    whether the next step should be a direct answer or a single tool attempt.
    """

    def prompt(self) -> str:
        return (
            "Before answering, produce ONE JSON planning object with exactly "
            'these fields: {"intent":"short intent","action":"answer|tool"}. '
            "Keep intent under 160 characters. Do not include reasoning, chain of thought, "
            "or extra fields."
        )

    def parse(self, text: str) -> Plan:
        try:
            data = json.loads(text.strip())
        except json.JSONDecodeError as exc:
            raise PlannerProtocolError("Planner returned invalid JSON") from exc
        if not isinstance(data, dict) or set(data) != {"intent", "action"}:
            raise PlannerProtocolError("Planner returned an invalid schema")
        intent = data.get("intent")
        action = data.get("action")
        if not isinstance(intent, str) or not intent.strip():
            raise PlannerProtocolError("Planner intent must be non-empty")
        if len(intent) > 160:
            raise PlannerProtocolError("Planner intent is too long")
        try:
            parsed_action = PlanAction(action)
        except ValueError as exc:
            raise PlannerProtocolError("Planner action must be 'answer' or 'tool'") from exc
        return Plan(intent=intent.strip(), action=parsed_action)
