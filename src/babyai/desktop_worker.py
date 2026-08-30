from __future__ import annotations

import json
import sys
import time
import traceback
from typing import TextIO

from .agent_desktop import DesktopCommands
from .desktop_commands import DesktopCommandError
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

    def write_message(message: dict[str, object]) -> None:
        # The protocol crosses Windows pipes whose inherited code page is not
        # guaranteed to be UTF-8. JsonDocument restores escaped Unicode in WinUI.
        output_stream.write(json.dumps(message, ensure_ascii=True) + "\n")
        output_stream.flush()

    try:
        for raw_line in input_stream:
            line = raw_line.rstrip("\r\n")
            if not line:
                continue

            request_id: object = None
            command = "unknown"
            should_stop = False
            response: dict[str, object] | None = None
            protocol = 1
            sequence = 0
            v2_active = False
            terminal_sent = False
            started = time.monotonic()

            def emit_v2(event: dict[str, object]) -> None:
                nonlocal sequence, terminal_sent
                event_name = event.get("event")
                if event_name not in {"state", "activity", "delta", "done", "error"}:
                    raise DesktopCommandError(
                        "Desktop worker emitted an invalid streaming event"
                    )
                if terminal_sent:
                    raise DesktopCommandError(
                        "Desktop worker emitted an event after the terminal event"
                    )
                if any(key in event for key in ("id", "protocol", "seq")):
                    raise DesktopCommandError(
                        "Desktop streaming envelope fields are worker-owned"
                    )
                write_message(
                    {
                        "id": request_id,
                        "protocol": 2,
                        "seq": sequence,
                        **event,
                    }
                )
                sequence += 1
                terminal_sent = event_name in {"done", "error"}

            try:
                if len(line) > MAX_WORKER_REQUEST_CHARS:
                    raise DesktopCommandError("Desktop worker request is too large")

                request = json.loads(line)
                if not isinstance(request, dict):
                    raise DesktopCommandError(
                        "Desktop worker request must be a JSON object"
                    )

                request_id = request.get("id")
                if (
                    isinstance(request_id, bool)
                    or not isinstance(request_id, int)
                    or request_id < 0
                ):
                    raise DesktopCommandError(
                        "Desktop worker id must be a non-negative integer"
                    )

                protocol_value = request.get("protocol", 1)
                if isinstance(protocol_value, bool) or not isinstance(
                    protocol_value, int
                ):
                    raise DesktopCommandError(
                        "Desktop worker protocol must be an integer"
                    )
                protocol = protocol_value
                if protocol not in {1, 2}:
                    raise DesktopCommandError(
                        "Unsupported desktop worker protocol"
                    )
                v2_active = protocol == 2

                command_value = request.get("command")
                if not isinstance(command_value, str) or not command_value.strip():
                    raise DesktopCommandError("Desktop worker command is required")
                command = command_value.strip()

                payload = request.get("payload", {})
                if not isinstance(payload, dict):
                    raise DesktopCommandError(
                        "Desktop worker payload must be a JSON object"
                    )

                trace(
                    "worker.command.start",
                    request_id=request_id,
                    command=command,
                )
                if protocol == 2:
                    if command != "chat":
                        raise DesktopCommandError(
                            "Desktop worker protocol 2 supports only chat"
                        )

                    def emit_stream_event(event: dict[str, object]) -> None:
                        if event.get("event") not in {"state", "activity", "delta"}:
                            raise DesktopCommandError(
                                "Desktop command emitted an invalid streaming event"
                            )
                        emit_v2(event)

                    result = command_surface.stream_chat(
                        payload,
                        emit_stream_event,
                    )
                    emit_v2(
                        {
                            "event": "done",
                            "ok": True,
                            "command": command,
                            "reply": result["reply"],
                            "metrics": result["metrics"],
                        }
                    )
                elif command == "worker.shutdown":
                    response = {
                        "id": request_id,
                        "ok": True,
                        "command": command,
                    }
                    should_stop = True
                else:
                    response = {
                        "id": request_id,
                        **command_surface.execute(command, payload),
                    }
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
                if v2_active:
                    if not terminal_sent:
                        emit_v2(
                            {
                                "event": "error",
                                "ok": False,
                                "error": str(exc),
                            }
                        )
                else:
                    response = {
                        "id": request_id,
                        "ok": False,
                        "error": str(exc),
                    }
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
                if v2_active:
                    if not terminal_sent:
                        emit_v2(
                            {
                                "event": "error",
                                "ok": False,
                                "error": "BabyAI desktop command failed.",
                            }
                        )
                else:
                    response = {
                        "id": request_id,
                        "ok": False,
                        "error": f"{type(exc).__name__}: {exc}",
                    }

            if response is not None:
                write_message(response)
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
