from __future__ import annotations

import json
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any


@dataclass(frozen=True, slots=True)
class PendingToolApproval:
    user_input: str
    tool: str
    arguments: dict[str, Any]
    capability: str


@dataclass(slots=True)
class PendingToolApprovalStore:
    path: Path

    def load(self) -> PendingToolApproval | None:
        if not self.path.exists():
            return None
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return None
        if not isinstance(data, dict):
            return None
        user_input = data.get("user_input")
        tool = data.get("tool")
        arguments = data.get("arguments")
        capability = data.get("capability")
        if not isinstance(user_input, str) or not user_input.strip():
            return None
        if not isinstance(tool, str) or not tool.strip():
            return None
        if not isinstance(arguments, dict):
            return None
        if not isinstance(capability, str) or not capability.strip():
            return None
        return PendingToolApproval(
            user_input=user_input.strip(),
            tool=tool.strip(),
            arguments=arguments,
            capability=capability.strip(),
        )

    def save(self, approval: PendingToolApproval) -> PendingToolApproval:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(
            json.dumps(asdict(approval), ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        return approval

    def clear(self) -> None:
        try:
            self.path.unlink(missing_ok=True)
        except OSError:
            pass
