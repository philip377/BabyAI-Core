from __future__ import annotations

import os
import sys
import time
from pathlib import Path


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
