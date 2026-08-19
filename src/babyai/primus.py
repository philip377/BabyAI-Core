import json
import re
from dataclasses import dataclass

from .agent import AgentExecutor, ToolCall, ToolProtocolError
from .identity import Identity
from .llm import LLMProvider
from .memory import MemoryKind, MemoryStore
from .planner import PlanAction, Planner, PlannerProtocolError
from .tool_approval import PendingToolApproval, PendingToolApprovalStore
from .working_memory import WorkingMemoryStore


@dataclass(slots=True)
class Primus:
    llm: LLMProvider
    memory: MemoryStore
    identity: Identity
    agent: AgentExecutor | None = None
    planner: Planner | None = None
    working_memory: WorkingMemoryStore | None = None
    tool_approvals: PendingToolApprovalStore | None = None
    repair_tool_calls: bool = False
    max_context_chars: int = 12_000

    def _base_prompt(self, user_input: str, *, include_tool_catalog: bool | None = None) -> str:
        if include_tool_catalog is None:
            include_tool_catalog = (
                self.agent is not None and self.agent.requests_local_action(user_input)
            )
        prompt_parts = [self.identity.system_context()]
        task = self.working_memory.load() if self.working_memory is not None else None
        if task is not None:
            prompt_parts.append(task.as_context())
        if self.agent is not None and include_tool_catalog:
            prompt_parts.append(self.agent.catalog())
        prompt_parts.append(f"USER: {user_input}")

        fixed = "\n\n".join(prompt_parts)
        remaining = max(self.max_context_chars - len(fixed), 0)

        memory_parts: list[str] = []
        sections = [
            (
                "Known facts:",
                [f"- {item.content}" for item in self.memory.recent(limit=20, kind=MemoryKind.FACT)],
            ),
            (
                "Relevant learned knowledge:",
                [f"- {item.content}" for item in self.memory.recent(limit=16, kind=MemoryKind.KNOWLEDGE)],
            ),
            (
                "Recent episodic memory:",
                [
                    f"{item.role.upper()}: {item.content}"
                    for item in self.memory.recent(limit=24, kind=MemoryKind.EPISODIC)
                ],
            ),
        ]

        for heading, lines in sections:
            chunk = self._fit_section(heading, lines, remaining)
            if chunk:
                memory_parts.append(chunk)
                remaining -= len(chunk) + 2
            if remaining <= 0:
                break

        all_parts = [self.identity.system_context()]
        if task is not None:
            all_parts.append(task.as_context())
        all_parts.extend(memory_parts)
        if self.agent is not None and include_tool_catalog:
            all_parts.append(self.agent.catalog())
        all_parts.append(f"USER: {user_input}")
        return "\n\n".join(all_parts)

    @staticmethod
    def _fit_section(heading: str, lines: list[str], budget: int) -> str:
        if budget <= len(heading):
            return ""
        selected: list[str] = []
        used = len(heading) + 1
        for line in reversed(lines):
            cost = len(line) + 1
            if used + cost > budget:
                break
            selected.append(line)
            used += cost
        if not selected:
            return ""
        selected.reverse()
        return heading + "\n" + "\n".join(selected)

    def _plan(self, user_input: str):
        if self.planner is None:
            return None
        planning_prompt = self._base_prompt(user_input) + "\n\n" + self.planner.prompt()
        raw = self.llm.generate(planning_prompt)
        return self.planner.parse(raw)

    @staticmethod
    def _tool_followup(base: str, call: ToolCall, tool_result: str) -> str:
        return (
            base
            + "\n\nThe tool call was executed successfully.\n"
            + f"TOOL: {call.name}\nRESULT:\n{tool_result}\n\n"
            + "Answer the user using only the relevant tool result. Do not emit another tool call. "
            + "Do not mention internal tool names or permission mechanics unless the user asks."
        )

    @staticmethod
    def _fast_local_tool_response(user_input: str, call: ToolCall, tool_result: str) -> str | None:
        """Answer only deterministic high-confidence local reads without another LLM pass."""

        inferred = AgentExecutor.infer_safe_local_intent(user_input)
        if inferred is None or inferred.name != call.name or inferred.arguments != call.arguments:
            return None
        if call.name != "filesystem.list":
            return None

        try:
            entries = json.loads(tool_result)
        except (json.JSONDecodeError, TypeError):
            return None
        if not isinstance(entries, list) or not all(isinstance(item, str) for item in entries):
            return None

        files = [item for item in entries if item and not item.endswith("/")]
        if files:
            return f"Например: {files[0]}"
        return "На рабочем столе я не нашёл файлов."

    @staticmethod
    def _permission_prompt(call: ToolCall) -> str:
        if call.name == "filesystem.list":
            return "Мне нужно ваше разрешение, чтобы один раз посмотреть список файлов в указанной папке."
        if call.name == "filesystem.read":
            return "Мне нужно ваше разрешение, чтобы один раз прочитать указанный файл."
        if call.name == "system.info":
            return "Мне нужно ваше разрешение, чтобы один раз посмотреть сведения об этом компьютере."
        if call.name == "process.list":
            return "Мне нужно ваше разрешение, чтобы один раз посмотреть список запущенных процессов."
        return "Мне нужно ваше разрешение, чтобы выполнить это действие один раз."

    def _repair_tool_call(self, base: str, first: str) -> ToolCall | None:
        if not self.repair_tool_calls or self.agent is None:
            return None
        mentioned = self.agent.mentioned_tool(first)
        if mentioned is None:
            return None
        repair_prompt = (
            base
            + "\n\nYour previous draft mentioned the local tool "
            + mentioned
            + " but did not return a valid tool call. Do not explain or reason. "
            + "Return exactly one JSON object now with fields tool and arguments, and nothing else. "
            + f"Use tool {mentioned}."
        )
        repaired = self.llm.generate(repair_prompt)
        return self.agent.parse_tool_call(repaired)

    def _conversational_fast_response(self, user_input: str) -> str | None:
        """Answer a tiny set of identity/safety questions without native tool confusion."""

        text = re.sub(r"\s+", " ", user_input.casefold()).strip()
        plain_text = re.sub(r'["«»“”]', "", text)
        identity_markers = (
            "кто ты", "чем можешь помочь", "что ты умеешь",
            "who are you", "what can you do", "how can you help",
        )
        if any(marker in plain_text for marker in identity_markers):
            return (
                f"Я {self.identity.name} — ваш персональный ИИ-помощник. "
                "Могу отвечать на вопросы, объяснять сложное, помогать с текстами и планами, "
                "а по вашему явному запросу — безопасно работать с доступными локальными данными."
            )

        safety_markers = (
            "что значит безопасно", "что означает безопасно", "почему безопасно",
            "what do you mean by safe", "what does safe mean",
        )
        if any(marker in plain_text for marker in safety_markers):
            return (
                "Безопасно — значит без скрытых действий: я использую локальные возможности "
                "только когда это действительно нужно для вашего запроса и запрашиваю разрешение "
                "перед доступом к защищённым данным."
            )
        return None

    def _contains_internal_tool_output(self, text: str) -> bool:
        if self.agent is None:
            return False
        try:
            if self.agent.parse_tool_call(text) is not None:
                return True
        except ToolProtocolError:
            return True
        return self.agent.mentioned_tool(text) is not None or "Available tools:" in text

    def _answer_without_internal_tool_json(self, user_input: str) -> str:
        prompt = self._base_prompt(user_input, include_tool_catalog=False)
        prompt += (
            "\n\nAnswer the user's question normally. Do not call, list, or discuss internal tools, "
            "and do not output JSON."
        )
        answer = self.llm.generate(prompt)
        if self._contains_internal_tool_output(answer):
            deterministic = self._conversational_fast_response(user_input)
            if deterministic is not None:
                return deterministic
            return "Я не буду выполнять неподходящее локальное действие. Чем ещё могу помочь?"
        return answer

    def _execute_or_request_approval(self, base: str, user_input: str, call: ToolCall) -> str:
        if self.agent is None:
            raise ToolProtocolError("Tool execution is unavailable")

        capability = self.agent.required_capability(call)
        if not self.agent.is_allowed(call):
            if self.tool_approvals is None:
                return self.agent.execute(call)
            self.tool_approvals.save(
                PendingToolApproval(
                    user_input=user_input,
                    tool=call.name,
                    arguments=call.arguments,
                    capability=capability.value,
                )
            )
            return self._permission_prompt(call)

        tool_result = self.agent.execute(call)
        fast_response = self._fast_local_tool_response(user_input, call, tool_result)
        if fast_response is not None:
            return fast_response
        followup_base = self._base_prompt(user_input, include_tool_catalog=False)
        return self.llm.generate(self._tool_followup(followup_base, call, tool_result))

    def approve_pending_tool(self) -> str:
        if self.agent is None or self.tool_approvals is None:
            raise ToolProtocolError("Tool approval is unavailable")
        pending = self.tool_approvals.load()
        if pending is None:
            raise ToolProtocolError("No pending tool approval")

        call = ToolCall(name=pending.tool, arguments=pending.arguments)
        capability = self.agent.required_capability(call)
        if capability.value != pending.capability:
            self.tool_approvals.clear()
            raise ToolProtocolError("Pending tool approval capability mismatch")

        base = self._base_prompt(pending.user_input, include_tool_catalog=False)
        try:
            tool_result = self.agent.execute_once(call)
            response = self._fast_local_tool_response(pending.user_input, call, tool_result)
            if response is None:
                response = self.llm.generate(self._tool_followup(base, call, tool_result))
        finally:
            self.tool_approvals.clear()

        self.memory.add("babyai", response, kind=MemoryKind.EPISODIC)
        return response

    def reject_pending_tool(self) -> str:
        if self.tool_approvals is None or self.tool_approvals.load() is None:
            raise ToolProtocolError("No pending tool approval")
        self.tool_approvals.clear()
        response = "Доступ не предоставлен. Я не выполнял это действие."
        self.memory.add("babyai", response, kind=MemoryKind.EPISODIC)
        return response

    def think(self, user_input: str) -> str:
        conversational_response = self._conversational_fast_response(user_input)
        if conversational_response is not None:
            self.memory.add("user", user_input, kind=MemoryKind.EPISODIC)
            self.memory.add("babyai", conversational_response, kind=MemoryKind.EPISODIC)
            return conversational_response

        if self.repair_tool_calls and self.agent is not None:
            direct_call = self.agent.infer_safe_local_intent(user_input)
            if direct_call is not None:
                base = self._base_prompt(user_input)
                response = self._execute_or_request_approval(base, user_input, direct_call)
                self.memory.add("user", user_input, kind=MemoryKind.EPISODIC)
                self.memory.add("babyai", response, kind=MemoryKind.EPISODIC)
                return response

        try:
            plan = self._plan(user_input)
        except PlannerProtocolError:
            plan = None

        base = self._base_prompt(user_input)
        if plan is not None:
            base += f"\n\nIntent: {plan.intent}"
            if plan.action is PlanAction.ANSWER:
                base += "\nAnswer the user directly. Do not call a tool."
            else:
                base += "\nUse at most one available tool if needed."

        first = self.llm.generate(base)
        response = first

        if self.agent is not None and (plan is None or plan.action is PlanAction.TOOL):
            try:
                blocked_tool_call = False
                call = self.agent.parse_tool_call(first)
                if (
                    call is not None
                    and call.name in self.agent.tool_names()
                    and not self.agent.tool_compatible_with_intent(user_input, call.name)
                ):
                    response = self._answer_without_internal_tool_json(user_input)
                    call = None
                    blocked_tool_call = True
                elif call is None and self.repair_tool_calls:
                    call = self._repair_tool_call(base, first)
                    if (
                        call is not None
                        and call.name in self.agent.tool_names()
                        and not self.agent.tool_compatible_with_intent(user_input, call.name)
                    ):
                        call = None
                        blocked_tool_call = True
                if call is not None:
                    response = self._execute_or_request_approval(base, user_input, call)
                elif (
                    not blocked_tool_call
                    and self.repair_tool_calls
                    and self.agent.mentioned_tool(first) is not None
                ):
                    response = "Я понял, что для этого нужно локальное действие, но не смог безопасно подготовить его параметры."
            except (PermissionError, ToolProtocolError, FileNotFoundError, NotADirectoryError, ValueError) as exc:
                response = f"I could not use the requested tool: {exc}"

        self.memory.add("user", user_input, kind=MemoryKind.EPISODIC)
        self.memory.add("babyai", response, kind=MemoryKind.EPISODIC)
        return response
