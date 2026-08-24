from __future__ import annotations

import json
import sys
import time
import traceback
from typing import TextIO

from .desktop_commands import DesktopCommandError, DesktopCommands
from .runtime_trace import trace


MAX_WORKER_REQUEST_CHARS = 1_048_576


def serve(
    commands: DesktopCommands | None = None,
    *,
    stdin: TextIO | None = None,
    stdout: TextIO | None = None,
) -> int:
    """Serve one JSON request per line until EOF or an explicit shutdown request."""

    input_stream = stdin or sys.stdin
    output_stream = stdout or sys.stdout
    original_stdout: TextIO | None = None
    if stdout is None:
        original_stdout = sys.stdout
        sys.stdout = sys.stderr

    owned_commands = commands is None
    command_surface = commands or DesktopCommands(persistent=True)

    try:
        for raw_line in input_stream:
            line = raw_line.rstrip("\r\n")
            if not line:
                continue

            request_id: object = None
            command = "unknown"
            should_stop = False
            started = time.monotonic()
            try:
                if len(line) > MAX_WORKER_REQUEST_CHARS:
                    raise DesktopCommandError("Desktop worker request is too large")

                request = json.loads(line)
                if not isinstance(request, dict):
                    raise DesktopCommandError("Desktop worker request must be a JSON object")

                request_id = request.get("id")
                if isinstance(request_id, bool) or not isinstance(request_id, int) or request_id < 0:
                    raise DesktopCommandError("Desktop worker id must be a non-negative integer")

                command_value = request.get("command")
                if not isinstance(command_value, str) or not command_value.strip():
                    raise DesktopCommandError("Desktop worker command is required")
                command = command_value.strip()

                payload = request.get("payload", {})
                if not isinstance(payload, dict):
                    raise DesktopCommandError("Desktop worker payload must be a JSON object")

                trace("worker.command.start", request_id=request_id, command=command)
                if command == "worker.shutdown":
                    response: dict[str, object] = {
                        "id": request_id,
                        "ok": True,
                        "command": command,
                    }
                    should_stop = True
                else:
                    response = {"id": request_id, **command_surface.execute(command, payload)}
                trace(
                    "worker.command.done",
                    request_id=request_id,
                    command=command,
                    elapsed_ms=round((time.monotonic() - started) * 1000),
                )
            except (json.JSONDecodeError, DesktopCommandError, ValueError) as exc:
                trace(
                    "worker.command.error",
                    request_id=request_id,
                    command=command,
                    elapsed_ms=round((time.monotonic() - started) * 1000),
                    error=type(exc).__name__,
                )
                response = {"id": request_id, "ok": False, "error": str(exc)}
            except Exception as exc:
                # One bad legacy state file or command must not kill the persistent
                # desktop worker. Keep the JSONL protocol alive and preserve the full
                # traceback on stderr for diagnostics.
                trace(
                    "worker.command.error",
                    request_id=request_id,
                    command=command,
                    elapsed_ms=round((time.monotonic() - started) * 1000),
                    error=type(exc).__name__,
                )
                traceback.print_exc(file=sys.stderr)
                response = {
                    "id": request_id,
                    "ok": False,
                    "error": f"{type(exc).__name__}: {exc}",
                }

            # The desktop protocol crosses Windows pipes whose inherited code page
            # is not guaranteed to be UTF-8. Keep the JSONL wire format ASCII-only;
            # json.loads/JsonDocument restore the original Unicode for the UI.
            # A non-ASCII model reply must never terminate the worker after the
            # command has already completed.
            output_stream.write(json.dumps(response, ensure_ascii=True) + "\n")
            output_stream.flush()
            if should_stop:
                break
    finally:
        if owned_commands:
            command_surface.close()
        if original_stdout is not None:
            sys.stdout = original_stdout

    return 0


def main() -> int:
    return serve()


if __name__ == "__main__":
    raise SystemExit(main())
