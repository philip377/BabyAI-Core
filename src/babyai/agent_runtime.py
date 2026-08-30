from __future__ import annotations

import json
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

from .agent import AgentExecutor, ToolCall, ToolProtocolError
from .tool_approval import PendingToolApproval, PendingToolApprovalStore


class ModelDrivenAgentExecutor(AgentExecutor):
    """Keep the safe executor while disabling the old pre-LLM intent shortcut."""

    @staticmethod
    def infer_safe_local_intent(user_input: str) -> ToolCall | None:
        del user_input
        return None


@dataclass(frozen=True, slots=True)
class AgentObservation:
    tool: str
    arguments: dict[str, Any]
    result: str
    activity: str

    def as_context(self) -> str:
        return (
            "Recent trusted agent observation (local machine evidence).\n"
            "Use it when it is relevant to the user's current request. Do not invent local files, "
            "processes, windows, system state, or action results beyond this observation. The "
            "requested local action has already completed: answer the current user directly from "
            "OBSERVATION and do not output another tool call or ask for permission again. If a "
            "later user request needs different or newer evidence, the agent may be requested then.\n"
            f"AGENT_ACTION: {self.tool}\n"
            f"ARGUMENTS: {json.dumps(self.arguments, ensure_ascii=False, separators=(',', ':'))}\n"
            f"OBSERVATION:\n{self.result}"
        )


@dataclass(frozen=True, slots=True)
class AgentRunResult:
    observation: AgentObservation | None = None
    approval_prompt: str | None = None

    @property
    def approval_required(self) -> bool:
        return self.approval_prompt is not None


@dataclass(slots=True)
class AgentRuntime:
    """Coordinate model-selected agent actions around the safe low-level executor.

    The LLM decides when an action is needed and emits a structured request. This runtime owns
    permission handoff, execution, human-readable activity, and the latest trusted observation.
    The executor remains the deny-by-default boundary that actually touches the operating system.
    """

    executor: AgentExecutor
    approvals: PendingToolApprovalStore | None = None
    _last_observation: AgentObservation | None = None

    def catalog(self) -> str:
        return self.executor.catalog()

    def tool_names(self) -> tuple[str, ...]:
        return self.executor.tool_names()

    def parse_request(self, text: str) -> ToolCall | None:
        return self.executor.parse_tool_call(text)

    def mentioned_tool(self, text: str) -> str | None:
        return self.executor.mentioned_tool(text)

    def tool_compatible_with_intent(self, user_input: str, tool_name: str) -> bool:
        return self.executor.tool_compatible_with_intent(user_input, tool_name)

    def requests_local_action(self, user_input: str) -> bool:
        return self.executor.requests_local_action(user_input)

    @property
    def last_activity(self) -> str | None:
        observation = self._last_observation
        return None if observation is None else observation.activity

    def observation_context(self) -> str:
        observation = self._last_observation
        return "" if observation is None else observation.as_context()

    def invoke(
        self,
        user_input: str,
        call: ToolCall,
        *,
        on_activity: Callable[[str], None] | None = None,
    ) -> AgentRunResult:
        call = self._canonicalize_model_call(user_input, call)
        capability = self.executor.required_capability(call)
        if not self.executor.is_allowed(call):
            if self.approvals is None:
                return AgentRunResult(
                    observation=self._execute(call, on_activity=on_activity, once=False)
                )
            self.approvals.save(
                PendingToolApproval(
                    user_input=user_input,
                    tool=call.name,
                    arguments=call.arguments,
                    capability=capability.value,
                )
            )
            return AgentRunResult(approval_prompt=self.permission_prompt(call))

        return AgentRunResult(
            observation=self._execute(call, on_activity=on_activity, once=False)
        )

    @staticmethod
    def _canonicalize_model_call(user_input: str, call: ToolCall) -> ToolCall:
        """Replace known model placeholder paths only for an explicit Desktop request."""

        if call.name != "filesystem.list":
            return call
        text = user_input.casefold()
        requests_desktop = "desktop" in text or ("рабоч" in text and "стол" in text)
        if not requests_desktop:
            return call
        raw_path = str(call.arguments.get("path", ".")).strip()
        normalized = raw_path.replace("\\", "/").rstrip("/").casefold()
        desktop_aliases = {
            "desktop",
            "~/desktop",
            "/home/user/desktop",
            "/users/user/desktop",
            "c:/users/user/desktop",
        }
        if normalized not in desktop_aliases:
            return call
        arguments = dict(call.arguments)
        arguments["path"] = "~/Desktop"
        return ToolCall(name=call.name, arguments=arguments)

    def approve_pending(
        self,
        *,
        on_activity: Callable[[str], None] | None = None,
    ) -> tuple[str, AgentObservation]:
        if self.approvals is None:
            raise ToolProtocolError("Tool approval is unavailable")
        pending = self.approvals.load()
        if pending is None:
            raise ToolProtocolError("No pending tool approval")

        call = ToolCall(name=pending.tool, arguments=pending.arguments)
        capability = self.executor.required_capability(call)
        if capability.value != pending.capability:
            self.approvals.clear()
            raise ToolProtocolError("Pending tool approval capability mismatch")

        # Consume before execution so a cancelled/crashed worker cannot replay the same grant.
        self.approvals.clear()
        observation = self._execute(call, on_activity=on_activity, once=True)
        return pending.user_input, observation

    def reject_pending(self) -> None:
        if self.approvals is None or self.approvals.load() is None:
            raise ToolProtocolError("No pending tool approval")
        self.approvals.clear()

    def _execute(
        self,
        call: ToolCall,
        *,
        on_activity: Callable[[str], None] | None,
        once: bool,
    ) -> AgentObservation:
        activity = self.activity_text(call)
        if on_activity is not None:
            on_activity(activity)
        result = self.executor.execute_once(call) if once else self.executor.execute(call)
        observation = AgentObservation(
            tool=call.name,
            arguments=dict(call.arguments),
            result=result,
            activity=activity,
        )
        self._last_observation = observation
        return observation

    @staticmethod
    def activity_text(call: ToolCall) -> str:
        if call.name == "filesystem.list":
            path = str(call.arguments.get("path", ".")).strip() or "."
            normalized = path.replace("\\", "/").rstrip("/")
            if normalized in {"~/Desktop", "Desktop"}:
                return "Проверяю рабочий стол…"
            return f"Проверяю папку {path}…"
        if call.name == "filesystem.read":
            return f"Читаю файл {call.arguments.get('path', '')}…"
        if call.name == "filesystem.write":
            return f"Записываю файл {call.arguments.get('path', '')}…"
        if call.name == "system.info":
            return "Проверяю сведения о компьютере…"
        if call.name == "process.list":
            return "Смотрю запущенные процессы…"
        if call.name == "application.open":
            return f"Открываю {call.arguments.get('name', 'приложение')}…"
        if call.name == "command.run":
            return f"Выполняю диагностику {call.arguments.get('command', '')}…"
        if call.name == "window.list":
            return "Смотрю открытые окна…"
        if call.name == "window.activate":
            return "Переключаюсь на окно…"
        if call.name == "system.lock":
            return "Блокирую рабочую станцию…"
        if call.name == "screen.capture":
            return "Делаю снимок экрана…"
        return "Выполняю локальное действие…"

    @staticmethod
    def permission_prompt(call: ToolCall) -> str:
        def shown(key: str, fallback: str) -> str:
            value = str(call.arguments.get(key, fallback)).strip() or fallback
            return value if len(value) <= 160 else value[:157] + "…"

        if call.name == "filesystem.list":
            return f"Разрешить один раз посмотреть список файлов в папке: {shown('path', '.')}?"
        if call.name == "filesystem.read":
            return f"Разрешить один раз прочитать файл: {shown('path', 'не указан')}?"
        if call.name == "filesystem.write":
            action = "перезаписать" if call.arguments.get("overwrite") is True else "создать"
            return f"Разрешить один раз {action} файл: {shown('path', 'не указан')}?"
        if call.name == "system.info":
            return "Мне нужно ваше разрешение, чтобы один раз посмотреть сведения об этом компьютере."
        if call.name == "process.list":
            return "Мне нужно ваше разрешение, чтобы один раз посмотреть список запущенных процессов."
        if call.name == "application.open":
            return f"Разрешить один раз открыть приложение: {shown('name', 'не указано')}?"
        if call.name == "command.run":
            return f"Разрешить один раз выполнить диагностическую команду: {shown('command', 'не указана')}?"
        if call.name == "window.list":
            return "Разрешить один раз посмотреть список открытых окон?"
        if call.name == "window.activate":
            return f"Разрешить один раз активировать окно с идентификатором {shown('handle', 'не указан')}?"
        if call.name == "system.lock":
            return "Разрешить один раз заблокировать рабочую станцию Windows?"
        if call.name == "screen.capture":
            mode = shown("mode", "active_window")
            target = "активного окна" if mode == "active_window" else "основного экрана"
            return f"Разрешить один раз сделать снимок {target}? На снимке могут быть личные данные."
        return "Мне нужно ваше разрешение, чтобы выполнить это действие один раз."
