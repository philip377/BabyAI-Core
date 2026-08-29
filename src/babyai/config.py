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
    native_vulkan_runtime_path: Path | None = None
    native_acceleration: str = "cpu"

    @classmethod
    def default(cls) -> "BabyAIConfig":
        data_dir = Path(os.getenv("BABYAI_DATA_DIR", Path.home() / ".babyai"))
        native_model = os.getenv("BABYAI_NATIVE_MODEL")
        native_runtime = os.getenv("BABYAI_NATIVE_RUNTIME")
        native_vulkan_runtime = os.getenv("BABYAI_NATIVE_VULKAN_RUNTIME")
        return cls(
            data_dir=data_dir,
            owner=os.getenv("BABYAI_OWNER", "owner"),
            name=os.getenv("BABYAI_NAME", "BabyAI"),
            provider=os.getenv("BABYAI_PROVIDER", "ollama").lower(),
            model=os.getenv("BABYAI_MODEL", "qwen3:8b"),
            ollama_url=os.getenv("BABYAI_OLLAMA_URL", "http://127.0.0.1:11434"),
            native_model_path=Path(native_model) if native_model else None,
            native_runtime_path=Path(native_runtime) if native_runtime else None,
            native_vulkan_runtime_path=Path(native_vulkan_runtime) if native_vulkan_runtime else None,
            native_acceleration=os.getenv("BABYAI_NATIVE_ACCELERATION", "cpu").strip().lower(),
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
    def native_vulkan_runtime_file(self) -> Path:
        if self.native_vulkan_runtime_path is not None:
            return self.native_vulkan_runtime_path
        cpu_runtime = self.native_runtime_file
        return cpu_runtime.parent / "vulkan" / cpu_runtime.name

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
    def pending_tool_approval_file(self) -> Path:
        return self.data_dir / "pending_tool_approval.json"

    @property
    def screen_captures_dir(self) -> Path:
        return self.data_dir / "screen_captures"

    @property
    def history_db(self) -> Path:
        return self.data_dir / "history.sqlite3"

    @property
    def history_settings_file(self) -> Path:
        return self.data_dir / "history.json"

    @property
    def workspace_file(self) -> Path:
        return self.data_dir / "workspaces.json"

    @property
    def workspace_tasks_dir(self) -> Path:
        return self.data_dir / "workspace_tasks"

    @property
    def workspace_documents_dir(self) -> Path:
        return self.data_dir / "workspace_documents"

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
