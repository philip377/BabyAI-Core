from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import Any, Callable

from .observer import Observer
from .permissions import PermissionStore
from .tools import Toolset


@dataclass(frozen=True, slots=True)
class ToolCall:
    name: str
    arguments: dict[str, Any]


class ToolProtocolError(ValueError):
    pass


@dataclass(slots=True)
class AgentExecutor:
    permissions: PermissionStore
    toolset: Toolset = field(init=False)
    observer: Observer = field(init=False)

    def __post_init__(self) -> None:
        self.toolset = Toolset(self.permissions)
        self.observer = Observer(self.permissions)

    def catalog(self) -> str:
        return (
            "Available tools:\n"
            "- system.info {}\n"
            "- filesystem.list {\"path\": \"...\"}\n"
            "- filesystem.read {\"path\": \"...\"}\n"
            "- process.list {}\n"
            "To call a tool, reply with ONLY one JSON object: "
            '{"tool":"tool.name","arguments":{...}}. '
            "If no tool is needed, answer normally."
        )

    def parse_tool_call(self, text: str) -> ToolCall | None:
        stripped = text.strip()
        if not stripped.startswith("{"):
            return None
        try:
            data = json.loads(stripped)
        except json.JSONDecodeError:
            return None
        if not isinstance(data, dict) or "tool" not in data:
            return None
        name = data.get("tool")
        arguments = data.get("arguments", {})
        if not isinstance(name, str) or not isinstance(arguments, dict):
            raise ToolProtocolError("Invalid tool call schema")
        return ToolCall(name=name, arguments=arguments)

    def execute(self, call: ToolCall) -> str:
        handlers: dict[str, Callable[[dict[str, Any]], object]] = {
            "system.info": lambda _: self.observer.system_snapshot().as_context(),
            "filesystem.list": lambda args: self.toolset.list_directory(args.get("path", ".")),
            "filesystem.read": lambda args: self.toolset.read_text(self._required_path(args)),
            "process.list": lambda _: self.toolset.list_processes(),
        }
        handler = handlers.get(call.name)
        if handler is None:
            raise ToolProtocolError(f"Unknown tool: {call.name}")
        result = handler(call.arguments)
        if isinstance(result, str):
            return result
        return json.dumps(result, ensure_ascii=False, indent=2)

    @staticmethod
    def _required_path(arguments: dict[str, Any]) -> str:
        path = arguments.get("path")
        if not isinstance(path, str) or not path.strip():
            raise ToolProtocolError("filesystem.read requires a non-empty path")
        return path
