from __future__ import annotations

import os
import sys
import time
import ctypes
from pathlib import Path


def process_memory_metrics() -> dict[str, float]:
    """Return best-effort process memory counters without adding a dependency."""

    try:
        if os.name == "nt":
            size_t = ctypes.c_size_t

            class ProcessMemoryCountersEx(ctypes.Structure):
                _fields_ = [
                    ("cb", ctypes.c_ulong),
                    ("page_fault_count", ctypes.c_ulong),
                    ("peak_working_set_size", size_t),
                    ("working_set_size", size_t),
                    ("quota_peak_paged_pool_usage", size_t),
                    ("quota_paged_pool_usage", size_t),
                    ("quota_peak_non_paged_pool_usage", size_t),
                    ("quota_non_paged_pool_usage", size_t),
                    ("pagefile_usage", size_t),
                    ("peak_pagefile_usage", size_t),
                    ("private_usage", size_t),
                ]

            counters = ProcessMemoryCountersEx()
            counters.cb = ctypes.sizeof(counters)
            handle = ctypes.windll.kernel32.GetCurrentProcess()
            ok = ctypes.windll.psapi.GetProcessMemoryInfo(
                handle,
                ctypes.byref(counters),
                counters.cb,
            )
            if ok:
                mib = 1024 * 1024
                return {
                    "working_set_mb": round(counters.working_set_size / mib, 1),
                    "private_mb": round(counters.private_usage / mib, 1),
                    "peak_working_set_mb": round(counters.peak_working_set_size / mib, 1),
                }
    except Exception:
        pass
    return {}


def trace(stage: str, **fields: object) -> None:
    """Write one best-effort native runtime diagnostic line.

    The desktop launcher opts into a file path with BABYAI_RUNTIME_LOG. CLI users
    remain quiet unless they explicitly set that variable. Diagnostics must never
    become an inference failure.
    """

    log_path = os.environ.get("BABYAI_RUNTIME_LOG", "").strip()
    if not log_path:
        return

    parts = [f"[{time.strftime('%Y-%m-%dT%H:%M:%S')}]", stage]
    for key, value in fields.items():
        text = str(value).replace("\r", " ").replace("\n", " ")
        parts.append(f"{key}={text}")
    line = " ".join(parts) + "\n"

    try:
        path = Path(log_path).expanduser()
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as stream:
            stream.write(line)
            stream.flush()
    except Exception:
        try:
            print(line.rstrip(), file=sys.stderr, flush=True)
        except Exception:
            pass
