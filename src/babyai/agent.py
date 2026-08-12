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
            "To call a tool, reply with exactly one JSON object and nothing else: "
            '{"tool":"tool.name","arguments":{...}}. '
            "A fenced ```json block containing only that object is also accepted. "
            "If no tool is needed, answer normally."
        )

    def parse_tool_call(self, text: str) -> ToolCall | None:
        stripped = self._unwrap_json_block(text)
        if not stripped.startswith("{"):
            return None
        try:
            data = json.loads(stripped)
        except json.JSONDecodeError:
            return None
        if not isinstance(data, dict) or "tool" not in data:
            return None

        allowed_keys = {"tool", "arguments"}
        unexpected = set(data) - allowed_keys
        if unexpected:
            raise ToolProtocolError(
                f"Unexpected tool call fields: {', '.join(sorted(unexpected))}"
            )

        name = data.get("tool")
        arguments = data.get("arguments", {})
        if not isinstance(name, str) or not name.strip():
            raise ToolProtocolError("Tool name must be a non-empty string")
        if not isinstance(arguments, dict):
            raise ToolProtocolError("Tool arguments must be a JSON object")
        return ToolCall(name=name.strip(), arguments=arguments)

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
    def _unwrap_json_block(text: str) -> str:
        stripped = text.strip()
        if not stripped.startswith("```"):
            return stripped

        lines = stripped.splitlines()
        if len(lines) < 3 or not lines[-1].strip().startswith("```"):
            return stripped
        first = lines[0].strip().lower()
        if first not in {"```", "```json"}:
            return stripped
        return "\n".join(lines[1:-1]).strip()

    @staticmethod
    def _required_path(arguments: dict[str, Any]) -> str:
        path = arguments.get("path")
        if not isinstance(path, str) or not path.strip():
            raise ToolProtocolError("filesystem.read requires a non-empty path")
        return path
