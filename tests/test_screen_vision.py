from __future__ import annotations

import json

import pytest

from babyai.agent import AgentExecutor, ToolCall
from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommands
from babyai.identity import Identity
from babyai.llm import LLMProvider
from babyai.memory import SQLiteMemoryStore
from babyai.permissions import Capability, PermissionStore
from babyai.primus import Primus
from babyai.screen_vision import ScreenCaptureStore
from babyai.tool_approval import PendingToolApprovalStore


class NoGenerationProvider(LLMProvider):
    def generate(self, prompt: str) -> str:
        raise AssertionError("screen capture must not pretend a text model analyzed pixels")


def test_screen_capture_is_permissioned_local_and_deletable(tmp_path, monkeypatch) -> None:
    monkeypatch.setattr(
        "babyai.screen_vision._capture_bmp",
        lambda mode: (b"BM-test", 640, 480, "Private document"),
    )
    permissions = PermissionStore(tmp_path / "permissions.json")
    executor = AgentExecutor(permissions)

    with pytest.raises(PermissionError):
        executor.execute(ToolCall("screen.capture", {"mode": "active_window"}))
    payload = json.loads(
        executor.execute_once(ToolCall("screen.capture", {"mode": "active_window"}))
    )

    capture = tmp_path / "screen_captures" / f"{payload['id']}.bmp"
    assert capture.read_bytes() == b"BM-test"
    assert payload["analysis_status"] == "capture_only"
    assert not permissions.is_granted(Capability.SCREEN_CAPTURE)
    store = ScreenCaptureStore(tmp_path / "screen_captures", permissions)
    assert store.get(payload["id"]) is not None
    assert store.delete(payload["id"])
    assert not capture.exists()


def test_capture_approval_warns_about_private_data_and_skips_generation(tmp_path, monkeypatch) -> None:
    monkeypatch.setattr(
        "babyai.screen_vision._capture_bmp",
        lambda mode: (b"BM-test", 320, 200, "Active window"),
    )
    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "pending_tool_approval.json")
    primus = Primus(
        llm=NoGenerationProvider(),
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=approvals,
    )

    prompt = primus._execute_or_request_approval(
        "base",
        "Сделай скриншот активного окна",
        ToolCall("screen.capture", {"mode": "active_window"}),
    )
    reply = primus.approve_pending_tool()

    assert "личные данные" in prompt
    assert "текстовая модель не анализирует пиксели" in reply
    assert approvals.load() is None
    assert not permissions.is_granted(Capability.SCREEN_CAPTURE)


def test_screen_capture_rejects_unbounded_or_unknown_modes(tmp_path) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    permissions.grant(Capability.SCREEN_CAPTURE)

    with pytest.raises(ValueError, match="active_window or primary_screen"):
        AgentExecutor(permissions).execute(
            ToolCall("screen.capture", {"mode": "all_monitors_forever"})
        )


def test_controlled_vision_action_only_creates_a_second_approval(tmp_path, monkeypatch) -> None:
    monkeypatch.setattr(
        "babyai.screen_vision._capture_bmp",
        lambda mode: (b"BM-test", 100, 100, "Desktop"),
    )
    config = BabyAIConfig(data_dir=tmp_path, provider="echo")
    permissions = PermissionStore(config.permissions_file)
    observation = json.loads(
        AgentExecutor(permissions).execute_once(
            ToolCall("screen.capture", {"mode": "active_window"})
        )
    )
    commands = DesktopCommands(config)

    result = commands.execute(
        "vision.action.propose",
        {
            "observation_id": observation["id"],
            "tool": "application.open",
            "arguments": {"name": "notepad"},
        },
    )

    pending = PendingToolApprovalStore(config.pending_tool_approval_file).load()
    assert "открыть приложение: notepad" in result["reply"]
    assert pending is not None
    assert pending.tool == "application.open"
    assert pending.capability == Capability.APPLICATION_OPEN.value
    assert not permissions.is_granted(Capability.APPLICATION_OPEN)


def test_controlled_vision_action_refuses_unrestricted_tools(tmp_path, monkeypatch) -> None:
    monkeypatch.setattr(
        "babyai.screen_vision._capture_bmp",
        lambda mode: (b"BM-test", 100, 100, "Desktop"),
    )
    config = BabyAIConfig(data_dir=tmp_path, provider="echo")
    observation = json.loads(
        AgentExecutor(PermissionStore(config.permissions_file)).execute_once(
            ToolCall("screen.capture", {"mode": "active_window"})
        )
    )

    with pytest.raises(Exception, match="application.open or window.activate"):
        DesktopCommands(config).execute(
            "vision.action.propose",
            {
                "observation_id": observation["id"],
                "tool": "command.run",
                "arguments": {"command": "whoami"},
            },
        )
