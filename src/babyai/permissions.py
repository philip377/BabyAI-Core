from __future__ import annotations

import json
from contextlib import contextmanager
from dataclasses import dataclass, field
from enum import StrEnum
from pathlib import Path
from typing import Iterator


class Capability(StrEnum):
    SYSTEM_INFO = "system.info"
    FILESYSTEM_LIST = "filesystem.list"
    FILESYSTEM_READ = "filesystem.read"
    PROCESS_LIST = "process.list"


@dataclass(slots=True)
class PermissionStore:
    path: Path
    _temporary: dict[Capability, int] = field(default_factory=dict, init=False, repr=False)

    def _load(self) -> set[Capability]:
        if not self.path.exists():
            return set()
        data = json.loads(self.path.read_text(encoding="utf-8"))
        raw = data.get("granted", [])
        granted: set[Capability] = set()
        for value in raw:
            try:
                granted.add(Capability(value))
            except ValueError:
                continue
        return granted

    def _save(self, granted: set[Capability]) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        payload = {"granted": sorted(item.value for item in granted)}
        self.path.write_text(json.dumps(payload, indent=2), encoding="utf-8")

    def list(self) -> list[Capability]:
        return sorted(self._load(), key=lambda item: item.value)

    def is_granted(self, capability: Capability) -> bool:
        return self._temporary.get(capability, 0) > 0 or capability in self._load()

    def grant(self, capability: Capability) -> None:
        granted = self._load()
        granted.add(capability)
        self._save(granted)

    def revoke(self, capability: Capability) -> None:
        granted = self._load()
        granted.discard(capability)
        self._save(granted)

    def require(self, capability: Capability) -> None:
        if not self.is_granted(capability):
            raise PermissionError(
                f"Capability '{capability.value}' is not granted. "
                f"Use: babyai permissions grant {capability.value}"
            )

    @contextmanager
    def temporary_grant(self, capability: Capability) -> Iterator[None]:
        """Grant one capability to this executor only, without persisting it."""

        self._temporary[capability] = self._temporary.get(capability, 0) + 1
        try:
            yield
        finally:
            remaining = self._temporary.get(capability, 0) - 1
            if remaining > 0:
                self._temporary[capability] = remaining
            else:
                self._temporary.pop(capability, None)
