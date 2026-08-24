from __future__ import annotations

import json
from dataclasses import asdict, dataclass, fields
from pathlib import Path


@dataclass(frozen=True, slots=True)
class Identity:
    name: str = "BabyAI"
    owner: str = "owner"
    purpose: str = "Learn and develop alongside the owner."
    version: str = "0.1"

    def system_context(self) -> str:
        return (
            f"You are {self.name} v{self.version}, a personal AI developing alongside "
            f"{self.owner}. Your purpose is: {self.purpose} "
            "Respond in the language of the user's latest message. Do not translate, "
            "repeat, or duplicate the answer in another language unless the user explicitly "
            "asks for a translation or a bilingual response. "
            "Preserve continuity, be curious, state uncertainty, and never perform "
            "sensitive external actions without explicit permission."
        )


class IdentityStore:
    def __init__(self, path: str | Path) -> None:
        self.path = Path(path)

    def load_or_create(self, default: Identity) -> Identity:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        if not self.path.exists():
            self.save(default)
            return default

        data = json.loads(self.path.read_text(encoding="utf-8"))
        if not isinstance(data, dict):
            raise ValueError("Identity state must be a JSON object")

        # Keep identity files forward/backward compatible across desktop releases.
        # Unknown keys from older/newer schemas must not crash the whole worker.
        allowed = {field.name for field in fields(Identity)}
        compatible = {key: value for key, value in data.items() if key in allowed}
        return Identity(**compatible)

    def save(self, identity: Identity) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(
            json.dumps(asdict(identity), indent=2, ensure_ascii=False),
            encoding="utf-8",
        )
