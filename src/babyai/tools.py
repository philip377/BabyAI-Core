from __future__ import annotations

import ctypes
import csv
import io
import locale
import os
import subprocess
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


def _windows_process_list() -> list[str]:
    """Read a bounded process snapshot through Windows' fixed tasklist command."""

    completed = subprocess.run(
        ["tasklist.exe", "/fo", "csv", "/nh"],
        check=True,
        capture_output=True,
        timeout=5,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    encoding = locale.getpreferredencoding(False) or "utf-8"
    output = completed.stdout.decode(encoding, errors="replace")
    results: list[str] = []
    for row in csv.reader(io.StringIO(output)):
        if len(row) < 2 or not row[1].strip().isdigit():
            continue
        results.append(f"{row[1].strip()}: {row[0].strip()}")
        if len(results) >= 200:
            break
    return results


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

    def write_text(
        self,
        path: str | Path,
        content: str,
        *,
        overwrite: bool = False,
        max_bytes: int = 262_144,
    ) -> str:
        self.permissions.require(Capability.FILESYSTEM_WRITE)
        if not isinstance(content, str):
            raise ValueError("filesystem.write content must be text")
        encoded = content.encode("utf-8")
        if len(encoded) > max_bytes:
            raise ValueError(f"Refusing to write {len(encoded)} bytes; limit is {max_bytes} bytes")
        if not isinstance(overwrite, bool):
            raise ValueError("filesystem.write overwrite must be true or false")
        target = _resolve_local_path(path)
        if not target.parent.is_dir():
            raise FileNotFoundError(f"Parent directory does not exist: {target.parent}")
        if target.exists() and not overwrite:
            raise FileExistsError(f"Refusing to overwrite existing file: {target}")
        if target.exists() and not target.is_file():
            raise IsADirectoryError(str(target))
        mode = "w" if overwrite else "x"
        with target.open(mode, encoding="utf-8", newline="") as stream:
            stream.write(content)
        return f"Wrote {len(encoded)} bytes to {target}"

    def list_processes(self) -> list[str]:
        self.permissions.require(Capability.PROCESS_LIST)
        if _is_windows():
            return _windows_process_list()
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
