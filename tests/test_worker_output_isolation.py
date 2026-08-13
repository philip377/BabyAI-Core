from __future__ import annotations

import io
import json
import sys

from babyai.desktop_worker import serve


class Commands:
    def execute(self, command, payload):
        print("diagnostic noise")
        return {"ok": True, "command": command}

    def close(self):
        pass


def test_cli_protocol_stays_clean(monkeypatch):
    protocol = io.StringIO()
    diagnostics = io.StringIO()
    source = io.StringIO(
        json.dumps({"id": 7, "command": "status", "payload": {}}) + "\n"
        + json.dumps({"id": 8, "command": "worker.shutdown", "payload": {}}) + "\n"
    )
    monkeypatch.setattr(sys, "stdin", source)
    monkeypatch.setattr(sys, "stdout", protocol)
    monkeypatch.setattr(sys, "stderr", diagnostics)

    assert serve(Commands()) == 0

    responses = [json.loads(line) for line in protocol.getvalue().splitlines()]
    assert [response["id"] for response in responses] == [7, 8]
    assert "diagnostic noise" not in protocol.getvalue()
    assert "diagnostic noise" in diagnostics.getvalue()
