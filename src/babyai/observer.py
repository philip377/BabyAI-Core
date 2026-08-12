from __future__ import annotations

import os
import platform
import socket
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

from .permissions import Capability, PermissionStore


@dataclass(frozen=True, slots=True)
class SystemSnapshot:
    captured_at: datetime
    hostname: str
    os_name: str
    os_release: str
    architecture: str
    python_version: str
    cwd: str
    home: str

    def as_context(self) -> str:
        return (
            f"hostname={self.hostname}; os={self.os_name} {self.os_release}; "
            f"arch={self.architecture}; python={self.python_version}; "
            f"cwd={self.cwd}; home={self.home}"
        )


@dataclass(slots=True)
class Observer:
    permissions: PermissionStore

    def system_snapshot(self) -> SystemSnapshot:
        self.permissions.require(Capability.SYSTEM_INFO)
        return SystemSnapshot(
            captured_at=datetime.now(timezone.utc),
            hostname=socket.gethostname(),
            os_name=platform.system(),
            os_release=platform.release(),
            architecture=platform.machine(),
            python_version=platform.python_version(),
            cwd=os.getcwd(),
            home=str(Path.home()),
        )
