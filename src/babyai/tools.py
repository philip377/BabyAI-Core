from __future__ import annotations

import os
import ctypes
import uuid
from dataclasses import dataclass
from pathlib import Path

from .permissions import Capability, PermissionStore


def _is_windows() -> bool:
    return os.name == "nt"


def _windows_desktop_directory() -> Path:
    """Resolve the user's real Windows Desktop, including OneDrive redirection."""

    folder_id = uuid.UUID("b4bfcc3a-db2c-424c-b029-7fe99a87c641")
    raw_guid = (ctypes.c_ubyte * 16).from_buffer_copy(folder_id.bytes_le)
    path = ctypes.c_wchar_p()
    shell32 = ctypes.WinDLL("shell32", use_last_error=True)
    ole32 = ctypes.WinDLL("ole32", use_last_error=True)
    shell32.SHGetKnownFolderPath.argtypes = [
        ctypes.c_void_p,
        ctypes.c_uint32,
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_wchar_p),
    ]
    shell32.SHGetKnownFolderPath.restype = ctypes.c_long
    result = shell32.SHGetKnownFolderPath(ctypes.byref(raw_guid), 0, None, ctypes.byref(path))
    if result != 0 or not path.value:
        raise OSError(f"Could not resolve the Windows Desktop known folder (HRESULT {result:#x})")
    try:
        return Path(path.value)
    finally:
        ole32.CoTaskMemFree(path)


def _resolve_local_path(path: str | Path) -> Path:
    raw = str(path)
    normalized = raw.replace("\\", "/").rstrip("/").casefold()
    if _is_windows() and normalized == "~/desktop":
        return _windows_desktop_directory().resolve()
    return Path(path).expanduser().resolve()


@dataclass(slots=True)
class Toolset:
    permissions: PermissionStore

    def list_directory(self, path: str | Path = ".") -> list[str]:
        self.permissions.require(Capability.FILESYSTEM_LIST)
        target = _resolve_local_path(path)
        if not target.is_dir():
            raise NotADirectoryError(str(target))
        return sorted(entry.name + ("/" if entry.is_dir() else "") for entry in target.iterdir())

    def read_text(self, path: str | Path, max_bytes: int = 262_144) -> str:
        self.permissions.require(Capability.FILESYSTEM_READ)
        target = _resolve_local_path(path)
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
