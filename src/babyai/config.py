from __future__ import annotations

import os
import sys
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
    native_model_path: Path | None = None
    native_runtime_path: Path | None = None

    @classmethod
    def default(cls) -> "BabyAIConfig":
        data_dir = Path(os.getenv("BABYAI_DATA_DIR", Path.home() / ".babyai"))
        native_model = os.getenv("BABYAI_NATIVE_MODEL")
        native_runtime = os.getenv("BABYAI_NATIVE_RUNTIME")
        return cls(
            data_dir=data_dir,
            owner=os.getenv("BABYAI_OWNER", "owner"),
            name=os.getenv("BABYAI_NAME", "BabyAI"),
            provider=os.getenv("BABYAI_PROVIDER", "ollama").lower(),
            model=os.getenv("BABYAI_MODEL", "qwen3:8b"),
            ollama_url=os.getenv("BABYAI_OLLAMA_URL", "http://127.0.0.1:11434"),
            native_model_path=Path(native_model) if native_model else None,
            native_runtime_path=Path(native_runtime) if native_runtime else None,
        )

    @property
    def native_model_file(self) -> Path:
        return self.native_model_path or (self.data_dir / "models" / "babyai.gguf")

    @property
    def native_runtime_file(self) -> Path:
        if self.native_runtime_path is not None:
            return self.native_runtime_path
        if os.name == "nt":
            filename = "babyai_native.dll"
        elif sys.platform == "darwin":
            filename = "libbabyai_native.dylib"
        else:
            filename = "libbabyai_native.so"
        return self.data_dir / "runtime" / filename

    @property
    def memory_db(self) -> Path:
        return self.data_dir / "memory.sqlite3"

    @property
    def identity_file(self) -> Path:
        return self.data_dir / "identity.json"

    @property
    def permissions_file(self) -> Path:
        return self.data_dir / "permissions.json"

    @property
    def working_memory_file(self) -> Path:
        return self.data_dir / "working_memory.json"

    @property
    def task_proposal_file(self) -> Path:
        return self.data_dir / "task_proposal.json"

    @property
    def hypothesis_file(self) -> Path:
        return self.data_dir / "hypothesis.json"

    @property
    def evidence_file(self) -> Path:
        return self.data_dir / "evidence.json"

    @property
    def lesson_candidate_file(self) -> Path:
        return self.data_dir / "lesson_candidate.json"

    @property
    def curiosity_file(self) -> Path:
        return self.data_dir / "curiosity.json"
