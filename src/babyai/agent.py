from __future__ import annotations

import json
import re
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

    @staticmethod
    def tool_names() -> tuple[str, ...]:
        return ("system.info", "filesystem.list", "filesystem.read", "process.list")

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
            "To call a tool, output exactly one JSON object as the entire response and put nothing before it: "
            '{"tool":"tool.name","arguments":{...}}. '
            "A fenced ```json block containing only that object is also accepted. "
            "If no tool is needed, answer normally."
        )

    @staticmethod
    def infer_safe_local_intent(user_input: str) -> ToolCall | None:
        """Recognise only high-confidence read-only desktop-list requests.

        This is deliberately narrow: it may create a pending approval, but can
        never execute the tool or grant permission. Ambiguous requests still go
        through the model.
        """

        text = re.sub(r"\s+", " ", user_input.casefold()).strip()
        desktop = "рабоч" in text and "стол" in text or "desktop" in text
        asks_for_entry = any(
            marker in text
            for marker in (
                "имя файла",
                "название файла",
                "какой файл",
                "какие файлы",
                "файлы на",
                "file name",
                "files on",
                "list files",
            )
        )
        if desktop and asks_for_entry:
            return ToolCall(name="filesystem.list", arguments={"path": "~/Desktop"})
        return None

    @staticmethod
    def tool_compatible_with_intent(user_input: str, tool_name: str) -> bool:
        """Allow a model-selected tool only for an explicit matching local request."""

        text = re.sub(r"\s+", " ", user_input.casefold()).strip()
        markers = {
            "system.info": (
                "system info", "system information", "computer specs", "pc specs",
                "what system", "inspect the system", "system is this", "system am i on",
                "сведения о компьютере", "информация о компьютере", "характеристик",
                "операционная система", "версия windows",
            ),
            "process.list": (
                "running process", "running app", "process list", "task manager",
                "запущенн", "процесс", "диспетчер задач",
            ),
            "filesystem.list": (
                "list files", "files in", "files on", "file name", "folder contents",
                "какие файлы", "список файлов", "файлы в", "файлы на", "имя файла",
                "название файла", "файл в", "содержимое папки", "что в папке", "рабочем столе",
                "посмотри рабочий стол", "покажи рабочий стол",
            ),
            "filesystem.read": (
                "read file", "open file", "file contents", "what is in the file",
                "read note", "open note",
                "прочитай файл", "открой файл", "содержимое файла", "что в файле",
            ),
        }
        return any(marker in text for marker in markers.get(tool_name, ()))

    @classmethod
    def requests_local_action(cls, user_input: str) -> bool:
        return any(cls.tool_compatible_with_intent(user_input, name) for name in cls.tool_names())

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

    def mentioned_tool(self, text: str) -> str | None:
        lower = text.lower()
        for name in self.tool_names():
            if name.lower() in lower:
                return name
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
        if self.permissions.is_granted(capability):
            return self.execute(call)
        with self.permissions.temporary_grant(capability):
            return self.execute(call)

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
