from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True, slots=True)
class BabyAIConfig:
    data_dir: Path
    owner: str = "owner"
    name: str = "BabyAI"

    @classmethod
    def default(cls) -> "BabyAIConfig":
        return cls(data_dir=Path.home() / ".babyai")

    @property
    def memory_db(self) -> Path:
        return self.data_dir / "memory.sqlite3"

    @property
    def identity_file(self) -> Path:
        return self.data_dir / "identity.json"
