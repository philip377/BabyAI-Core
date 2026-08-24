from __future__ import annotations

import ctypes
import locale
import os
import subprocess
from dataclasses import dataclass

from .permissions import Capability, PermissionStore


APPLICATIONS: dict[str, tuple[str, ...]] = {
    "calculator": ("calc.exe",),
    "explorer": ("explorer.exe",),
    "notepad": ("notepad.exe",),
    "settings": ("explorer.exe", "ms-settings:"),
}

DIAGNOSTIC_COMMANDS: dict[str, tuple[str, ...]] = {
    "hostname": ("hostname.exe",),
    "ipconfig": ("ipconfig.exe",),
    "whoami": ("whoami.exe",),
}


def _require_windows() -> None:
    if os.name != "nt":
        raise OSError("This action is available only on Windows")


def _launch_fixed(command: tuple[str, ...]) -> None:
    _require_windows()
    subprocess.Popen(
        list(command),
        close_fds=True,
        shell=False,
    )


def _run_fixed(command: tuple[str, ...]) -> str:
    _require_windows()
    completed = subprocess.run(
        list(command),
        check=False,
        capture_output=True,
        timeout=10,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        shell=False,
    )
    encoding = locale.getpreferredencoding(False) or "utf-8"
    raw = completed.stdout + completed.stderr
    if len(raw) > 65_536:
        raw = raw[:65_536]
    output = raw.decode(encoding, errors="replace").strip()
    if completed.returncode != 0:
        raise OSError(f"Diagnostic command failed with exit code {completed.returncode}: {output}")
    return output


def _visible_windows() -> list[dict[str, object]]:
    _require_windows()
    user32 = ctypes.windll.user32
    results: list[dict[str, object]] = []
    callback_type = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)

    def visit(hwnd, _lparam) -> bool:
        if not user32.IsWindowVisible(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        if length <= 0:
            return True
        buffer = ctypes.create_unicode_buffer(min(length + 1, 1024))
        user32.GetWindowTextW(hwnd, buffer, len(buffer))
        title = buffer.value.strip()
        if title:
            results.append({"handle": int(hwnd), "title": title})
        return len(results) < 100

    callback = callback_type(visit)
    if not user32.EnumWindows(callback, 0):
        error = ctypes.get_last_error()
        if error:
            raise OSError(error, "Could not enumerate Windows windows")
    return results


def _activate_window(handle: int) -> None:
    _require_windows()
    user32 = ctypes.windll.user32
    if not user32.IsWindow(handle) or not user32.IsWindowVisible(handle):
        raise ValueError("Window handle is not a visible top-level window")
    user32.ShowWindow(handle, 9)  # SW_RESTORE
    if not user32.SetForegroundWindow(handle):
        raise OSError("Windows refused to activate the selected window")


def _lock_workstation() -> None:
    _require_windows()
    if not ctypes.windll.user32.LockWorkStation():
        raise OSError("Windows refused to lock the workstation")


@dataclass(slots=True)
class WindowsActions:
    permissions: PermissionStore

    def open_application(self, name: str) -> str:
        self.permissions.require(Capability.APPLICATION_OPEN)
        command = APPLICATIONS.get(name.strip().casefold())
        if command is None:
            allowed = ", ".join(sorted(APPLICATIONS))
            raise ValueError(f"Application must be one of: {allowed}")
        _launch_fixed(command)
        return f"Opened {name.strip().casefold()}."

    def run_diagnostic(self, command_name: str) -> str:
        self.permissions.require(Capability.COMMAND_RUN)
        command = DIAGNOSTIC_COMMANDS.get(command_name.strip().casefold())
        if command is None:
            allowed = ", ".join(sorted(DIAGNOSTIC_COMMANDS))
            raise ValueError(f"Diagnostic command must be one of: {allowed}")
        return _run_fixed(command)

    def list_windows(self) -> list[dict[str, object]]:
        self.permissions.require(Capability.WINDOW_LIST)
        return _visible_windows()

    def activate_window(self, handle: int) -> str:
        self.permissions.require(Capability.WINDOW_ACTIVATE)
        if isinstance(handle, bool) or not isinstance(handle, int) or handle <= 0:
            raise ValueError("window.activate requires a positive integer handle")
        _activate_window(handle)
        return f"Activated window {handle}."

    def lock_workstation(self) -> str:
        self.permissions.require(Capability.SYSTEM_LOCK)
        _lock_workstation()
        return "Workstation locked."
