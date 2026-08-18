from __future__ import annotations

import ctypes
import os
from ctypes import wintypes


_RELATION_PROCESSOR_CORE = 0


def _windows_physical_cpu_count() -> int | None:
    """Return physical CPU cores through WinAPI without WMI or extra dependencies."""

    if os.name != "nt":
        return None

    try:
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        query = kernel32.GetLogicalProcessorInformationEx
        query.argtypes = [wintypes.DWORD, ctypes.c_void_p, ctypes.POINTER(wintypes.DWORD)]
        query.restype = wintypes.BOOL

        byte_count = wintypes.DWORD(0)
        query(_RELATION_PROCESSOR_CORE, None, ctypes.byref(byte_count))
        if byte_count.value < 8:
            return None

        buffer = (ctypes.c_ubyte * byte_count.value)()
        if not query(_RELATION_PROCESSOR_CORE, buffer, ctypes.byref(byte_count)):
            return None

        offset = 0
        physical_cores = 0
        while offset + 8 <= byte_count.value:
            relationship = int.from_bytes(bytes(buffer[offset : offset + 4]), "little")
            entry_size = int.from_bytes(bytes(buffer[offset + 4 : offset + 8]), "little")
            if entry_size < 8 or offset + entry_size > byte_count.value:
                return None
            if relationship == _RELATION_PROCESSOR_CORE:
                physical_cores += 1
            offset += entry_size

        return physical_cores or None
    except (AttributeError, OSError, ValueError):
        return None


def preferred_native_thread_count(*, logical_cpu_count: int | None = None) -> int:
    """Choose a conservative llama.cpp thread count, preferring physical cores on Windows."""

    logical = logical_cpu_count if logical_cpu_count is not None else (os.cpu_count() or 1)
    logical = max(int(logical), 1)
    physical = _windows_physical_cpu_count()
    available = physical if physical is not None and physical > 0 else logical
    return max(1, min(available, 8))
