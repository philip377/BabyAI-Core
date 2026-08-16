from __future__ import annotations

import json
from dataclasses import dataclass, field
from typing import Any, Callable

from .observer import Observer
from .permissions import Capability, PermissionStore
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
            "If the user's request requires observing the local computer and one of these tools can answer it, "
            "call the tool immediately. Do not discuss which tools exist, do not explain permission mechanics, "
            "and do not ask the user to grant permission yourself; the BabyAI host handles permission prompts. "
            "For the Windows desktop, use ~/Desktop when the user says 'desktop' or 'рабочий стол'. "
            "To call a tool, output exactly one JSON object: "
            '{"tool":"tool.name","arguments":{...}}. '
            "A fenced ```json block containing only that object is also accepted. "
            "If no tool is needed, answer normally."
        )

    def parse_tool_call(self, text: str) -> ToolCall | None:
        for data in self._tool_payload_candidates(text):
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
        return None

    def required_capability(self, call: ToolCall) -> Capability:
        capabilities = {
            "system.info": Capability.SYSTEM_INFO,
            "filesystem.list": Capability.FILESYSTEM_LIST,
            "filesystem.read": Capability.FILESYSTEM_READ,
            "process.list": Capability.PROCESS_LIST,
        }
        capability = capabilities.get(call.name)
        if capability is None:
            raise ToolProtocolError(f"Unknown tool: {call.name}")
        return capability

    def is_allowed(self, call: ToolCall) -> bool:
        return self.permissions.is_granted(self.required_capability(call))

    def execute_once(self, call: ToolCall) -> str:
        capability = self.required_capability(call)
        already_granted = self.permissions.is_granted(capability)
        if not already_granted:
            self.permissions.grant(capability)
        try:
            return self.execute(call)
        finally:
            if not already_granted:
                self.permissions.revoke(capability)

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

    @classmethod
    def _tool_payload_candidates(cls, text: str) -> list[dict[str, Any]]:
        stripped = cls._unwrap_json_block(text)
        candidates: list[dict[str, Any]] = []

        try:
            whole = json.loads(stripped)
        except json.JSONDecodeError:
            whole = None
        if isinstance(whole, dict) and "tool" in whole:
            candidates.append(whole)

        decoder = json.JSONDecoder()
        for index, char in enumerate(stripped):
            if char != "{":
                continue
            try:
                value, _ = decoder.raw_decode(stripped[index:])
            except json.JSONDecodeError:
                continue
            if isinstance(value, dict) and "tool" in value and value not in candidates:
                candidates.append(value)

        return list(reversed(candidates))

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
