from __future__ import annotations

import json
import sys
from typing import TextIO

from .desktop_commands import DesktopCommandError, DesktopCommands


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
            should_stop = False
            try:
                if len(line) > MAX_WORKER_REQUEST_CHARS:
                    raise DesktopCommandError("Desktop worker request is too large")

                request = json.loads(line)
                if not isinstance(request, dict):
                    raise DesktopCommandError("Desktop worker request must be a JSON object")

                request_id = request.get("id")
                if isinstance(request_id, bool) or not isinstance(request_id, int) or request_id < 0:
                    raise DesktopCommandError("Desktop worker id must be a non-negative integer")

                command = request.get("command")
                if not isinstance(command, str) or not command.strip():
                    raise DesktopCommandError("Desktop worker command is required")
                command = command.strip()

                payload = request.get("payload", {})
                if not isinstance(payload, dict):
                    raise DesktopCommandError("Desktop worker payload must be a JSON object")

                if command == "worker.shutdown":
                    response: dict[str, object] = {
                        "id": request_id,
                        "ok": True,
                        "command": command,
                    }
                    should_stop = True
                else:
                    response = {"id": request_id, **command_surface.execute(command, payload)}
            except (json.JSONDecodeError, DesktopCommandError, ValueError) as exc:
                response = {"id": request_id, "ok": False, "error": str(exc)}

            output_stream.write(json.dumps(response, ensure_ascii=False) + "\n")
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
