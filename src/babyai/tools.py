from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

from .permissions import Capability, PermissionStore


@dataclass(slots=True)
class Toolset:
    permissions: PermissionStore

    def list_directory(self, path: str | Path = ".") -> list[str]:
        self.permissions.require(Capability.FILESYSTEM_LIST)
        target = Path(path).expanduser().resolve()
        if not target.is_dir():
            raise NotADirectoryError(str(target))
        return sorted(entry.name + ("/" if entry.is_dir() else "") for entry in target.iterdir())

    def read_text(self, path: str | Path, max_bytes: int = 262_144) -> str:
        self.permissions.require(Capability.FILESYSTEM_READ)
        target = Path(path).expanduser().resolve()
        if not target.is_file():
            raise FileNotFoundError(str(target))
        size = target.stat().st_size
        if size > max_bytes:
            raise ValueError(f"Refusing to read {size} bytes; limit is {max_bytes} bytes")
        return target.read_text(encoding="utf-8", errors="replace")

    def list_processes(self) -> list[str]:
        self.permissions.require(Capability.PROCESS_LIST)
        proc = Path("/proc")
        if os.name != "posix" or not proc.exists():
            return ["Process listing is not implemented for this operating system yet."]
        results: list[str] = []
        for entry in proc.iterdir():
            if not entry.name.isdigit():
                continue
            try:
                name = (entry / "comm").read_text(encoding="utf-8").strip()
                results.append(f"{entry.name}: {name}")
            except OSError:
                continue
        return results[:200]
