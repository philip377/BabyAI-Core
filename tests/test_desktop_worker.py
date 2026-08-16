from __future__ import annotations

import io
import json
from types import SimpleNamespace

from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommands
from babyai.desktop_worker import serve


class FakeResidentProvider:
    def __init__(self, **kwargs):
        self.kwargs = kwargs
        self.closed = False

    def generate(self, prompt):
        return "ok"

    def close(self):
        self.closed = True


def test_persistent_desktop_commands_reuses_one_native_provider(tmp_path, monkeypatch):
    created = []

    def build(**kwargs):
        provider = FakeResidentProvider(**kwargs)
        created.append(provider)
        return provider

    monkeypatch.setattr(
        "babyai.desktop_commands.select_native_runtime",
        lambda *args: SimpleNamespace(runtime_path=tmp_path / "runtime.dll", n_gpu_layers=-1),
    )
    monkeypatch.setattr("babyai.desktop_commands.ResidentNativeBrainProvider", build)
    config = BabyAIConfig(
        data_dir=tmp_path,
        provider="native",
        native_acceleration="auto",
    )
    commands = DesktopCommands(config, persistent=True)

    first = commands._provider()
    second = commands._provider()

    assert first is second
    assert len(created) == 1
    assert created[0].kwargs["model_path"] == config.native_model_file
    assert created[0].kwargs["runtime_path"] == (tmp_path / "runtime.dll")
    assert created[0].kwargs["n_gpu_layers"] == -1
    assert created[0].kwargs["n_threads"] >= 1

    commands.close()
    commands.close()
    assert created[0].closed is True


def test_non_persistent_desktop_commands_keep_fresh_provider_behavior(tmp_path, monkeypatch):
    created = []

    def build(config):
        provider = FakeResidentProvider(index=len(created))
        created.append(provider)
        return provider

    monkeypatch.setattr("babyai.desktop_commands.build_brain_provider", build)
    commands = DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider="echo"))

    assert commands._provider() is not commands._provider()
    assert len(created) == 2


class FakeCommands:
    def __init__(self):
        self.calls = []
        self.closed = False

    def execute(self, command, payload):
        self.calls.append((command, payload))
        return {"ok": True, "command": command, "echo": payload}

    def close(self):
        self.closed = True


def test_worker_keeps_order_and_stops_on_shutdown():
    commands = FakeCommands()
    source = io.StringIO(
        "not-json\n"
        + json.dumps({"id": 1, "command": "status", "payload": {}})
        + "\n"
        + json.dumps({"id": 2, "command": "worker.shutdown", "payload": {}})
        + "\n"
        + json.dumps({"id": 3, "command": "status", "payload": {}})
        + "\n"
    )
    output = io.StringIO()

    assert serve(commands, stdin=source, stdout=output) == 0

    responses = [json.loads(line) for line in output.getvalue().splitlines()]
    assert responses[0]["ok"] is False
    assert responses[0]["id"] is None
    assert responses[1]["id"] == 1
    assert responses[1]["command"] == "status"
    assert responses[2] == {"id": 2, "ok": True, "command": "worker.shutdown"}
    assert commands.calls == [("status", {})]
    assert commands.closed is False


def test_worker_closes_commands_it_owns(monkeypatch):
    commands = FakeCommands()
    monkeypatch.setattr("babyai.desktop_worker.DesktopCommands", lambda persistent: commands)
    source = io.StringIO(json.dumps({"id": 1, "command": "worker.shutdown"}) + "\n")

    assert serve(stdin=source, stdout=io.StringIO()) == 0
    assert commands.closed is True


class ExplodingCommands:
    def __init__(self):
        self.calls = 0

    def execute(self, command, payload):
        self.calls += 1
        if self.calls == 1:
            raise RuntimeError("legacy state broke")
        return {"ok": True, "command": command}

    def close(self):
        pass


def test_worker_survives_unexpected_command_error():
    commands = ExplodingCommands()
    source = io.StringIO(
        json.dumps({"id": 1, "command": "status", "payload": {}})
        + "\n"
        + json.dumps({"id": 2, "command": "status", "payload": {}})
        + "\n"
    )
    output = io.StringIO()

    assert serve(commands, stdin=source, stdout=output) == 0

    responses = [json.loads(line) for line in output.getvalue().splitlines()]
    assert responses[0] == {
        "id": 1,
        "ok": False,
        "error": "RuntimeError: legacy state broke",
    }
    assert responses[1]["id"] == 2
    assert responses[1]["ok"] is True
    assert responses[1]["command"] == "status"
