from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True, slots=True)
class BabyAIConfig:
    data_dir: Path
    owner: str = "owner"
    name: str = "BabyAI"
    provider: str = "ollama"
    model: str = "qwen3:8b"
    ollama_url: str = "http://127.0.0.1:11434"

    @classmethod
    def default(cls) -> "BabyAIConfig":
        return cls(
            data_dir=Path(os.getenv("BABYAI_DATA_DIR", Path.home() / ".babyai")),
            owner=os.getenv("BABYAI_OWNER", "owner"),
            name=os.getenv("BABYAI_NAME", "BabyAI"),
            provider=os.getenv("BABYAI_PROVIDER", "ollama").lower(),
            model=os.getenv("BABYAI_MODEL", "qwen3:8b"),
            ollama_url=os.getenv("BABYAI_OLLAMA_URL", "http://127.0.0.1:11434"),
        )

    @property
    def memory_db(self) -> Path:
        return self.data_dir / "memory.sqlite3"

    @property
    def identity_file(self) -> Path:
        return self.data_dir / "identity.json"

    @property
    def permissions_file(self) -> Path:
        return self.data_dir / "permissions.json"
