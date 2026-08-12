from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from .config import BabyAIConfig
from .identity import Identity, IdentityStore
from .memory import SQLiteMemoryStore
from .permissions import PermissionStore


@dataclass(slots=True)
class DiagnosticCheck:
    name: str
    ok: bool
    detail: str


@dataclass(slots=True)
class DiagnosticReport:
    checks: list[DiagnosticCheck]

    @property
    def ok(self) -> bool:
        return all(check.ok for check in self.checks)


def initialize_data_dir(config: BabyAIConfig) -> list[Path]:
    config.data_dir.mkdir(parents=True, exist_ok=True)
    identity_store = IdentityStore(config.identity_file)
    identity_store.load_or_create(Identity(name=config.name, owner=config.owner))
    SQLiteMemoryStore(config.memory_db)
    PermissionStore(config.permissions_file)
    return [config.data_dir, config.identity_file, config.memory_db]


def run_local_diagnostics(config: BabyAIConfig) -> DiagnosticReport:
    checks: list[DiagnosticCheck] = []

    try:
        config.data_dir.mkdir(parents=True, exist_ok=True)
        probe = config.data_dir / ".doctor_probe"
        probe.write_text("ok", encoding="utf-8")
        read_back = probe.read_text(encoding="utf-8")
        probe.unlink()
        checks.append(DiagnosticCheck("data_dir", read_back == "ok", str(config.data_dir)))
    except OSError as exc:
        checks.append(DiagnosticCheck("data_dir", False, str(exc)))

    try:
        identity = IdentityStore(config.identity_file).load_or_create(
            Identity(name=config.name, owner=config.owner)
        )
        checks.append(DiagnosticCheck("identity", bool(identity.name and identity.owner), str(config.identity_file)))
    except (OSError, ValueError) as exc:
        checks.append(DiagnosticCheck("identity", False, str(exc)))

    try:
        memory = SQLiteMemoryStore(config.memory_db)
        memory.recent(limit=1)
        checks.append(DiagnosticCheck("memory", True, str(config.memory_db)))
    except (OSError, ValueError) as exc:
        checks.append(DiagnosticCheck("memory", False, str(exc)))

    try:
        permissions = PermissionStore(config.permissions_file)
        granted = [cap.value for cap in permissions.granted()]
        detail = "none granted" if not granted else f"granted={','.join(granted)}"
        checks.append(DiagnosticCheck("permissions", True, detail))
    except (OSError, ValueError) as exc:
        checks.append(DiagnosticCheck("permissions", False, str(exc)))

    return DiagnosticReport(checks)
