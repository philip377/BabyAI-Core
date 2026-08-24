import json
import re
from collections.abc import Callable
from dataclasses import dataclass

from .agent import AgentExecutor, ToolCall, ToolProtocolError
from .identity import Identity
from .llm import LLMError, LLMProvider
from .memory import MemoryKind, MemoryStore
from .planner import PlanAction, Planner, PlannerProtocolError
from .streaming import (
    StreamMetrics,
    StreamReply,
    StreamingSafetyError,
    VisibleTextGate,
    new_visible_marker,
    with_visible_marker_contract,
)
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
    session_memory: MemoryStore | None = None

    def _safe_memory_content(self, content: str) -> bool:
        """Keep tool protocol artifacts and old recovery messages out of prompts."""

        recovery_markers = (
            "Available tools:",
            "Я не буду выполнять неподходящее локальное действие.",
            "Я не смог безопасно сформировать ответ.",
            "I could not use the requested tool:",
        )
        if any(marker in content for marker in recovery_markers):
            return False
        return not self._contains_internal_tool_output(content)

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
                "User preferences:",
                [
                    f"- {item.content}"
                    for item in self.memory.recent(
                        limit=20,
                        kind=MemoryKind.PREFERENCE,
                        scope="global",
                    )
                    if self._safe_memory_content(item.content)
                ],
            ),
            (
                "Known facts:",
                [
                    f"- {item.content}"
                    for item in self.memory.recent(
                        limit=20,
                        kind=MemoryKind.FACT,
                        scope="global",
                    )
                    if self._safe_memory_content(item.content)
                ],
            ),
            (
                "Relevant learned knowledge:",
                [
                    f"- {item.content}"
                    for item in self.memory.recent(
                        limit=16,
                        kind=MemoryKind.KNOWLEDGE,
                        scope="global",
                    )
                    if self._safe_memory_content(item.content)
                ],
            ),
            (
                "Project memory:",
                []
                if task is None or not task.project
                else [
                    f"- {item.content}"
                    for item in self.memory.recent(
                        limit=20,
                        kind=MemoryKind.PROJECT,
                        scope=task.project,
                    )
                    if self._safe_memory_content(item.content)
                ],
            ),
            (
                "Recent episodic memory:",
                [
                    f"{item.role.upper()}: {item.content}"
                    for item in (self.session_memory or self.memory).recent(
                        limit=24,
                        kind=MemoryKind.EPISODIC,
                    )
                    if self._safe_memory_content(item.content)
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
    def _completed_action_response(call: ToolCall) -> str | None:
        messages = {
            "application.open": "Приложение открыто.",
            "filesystem.write": "Файл записан.",
            "window.activate": "Окно активировано.",
            "system.lock": "Рабочая станция заблокирована.",
            "screen.capture": (
                "Снимок сохранён локально. Текущая текстовая модель не анализирует пиксели; "
                "дальнейшее действие можно только предложить отдельно и подтвердить ещё раз."
            ),
        }
        return messages.get(call.name)

    @staticmethod
    def _permission_prompt(call: ToolCall) -> str:
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

    def _answer_without_internal_tool_json(self, user_input: str) -> tuple[str, bool]:
        # Recovery must be independent of working/episodic memory: either can contain
        # the hallucinated tool call that caused this path in the first place.
        prompt = (
            self.identity.system_context()
            + f"\n\nUSER: {user_input}"
            + "\n\nAnswer the user's message as a normal conversation. Do not call, list, "
            + "name, or discuss internal tools. Do not output JSON or technical protocol data."
        )
        for attempt in range(2):
            retry_prompt = prompt
            if attempt:
                retry_prompt += "\nYour previous draft was invalid. Reply only in natural language."
            answer = self.llm.generate(retry_prompt)
            if not self._contains_internal_tool_output(answer):
                return answer, True

        deterministic = self._conversational_fast_response(user_input)
        if deterministic is not None:
            return deterministic, True
        # Do not remember this last-resort reply. This prevents a transient model
        # failure from becoming the repeated answer to every subsequent message.
        return (
            "Я здесь. Давайте продолжим обычный разговор — что вы хотите узнать или сделать?",
            False,
        )

    def _execute_or_request_approval(
        self,
        base: str,
        user_input: str,
        call: ToolCall,
        *,
        on_state: Callable[[str], None] | None = None,
    ) -> str:
        if self.agent is None:
            raise ToolProtocolError("Tool execution is unavailable")

        capability = self.agent.required_capability(call)
        if not self.agent.is_allowed(call):
            if self.tool_approvals is None:
                if on_state is not None:
                    on_state("executing")
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

        if on_state is not None:
            on_state("executing")
        tool_result = self.agent.execute(call)
        fast_response = self._fast_local_tool_response(user_input, call, tool_result)
        if fast_response is None:
            fast_response = self._completed_action_response(call)
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

        # Consume before execution. If the Desktop cancels by terminating the worker,
        # the same approval cannot be replayed after restart.
        self.tool_approvals.clear()
        base = self._base_prompt(pending.user_input, include_tool_catalog=False)
        tool_result = self.agent.execute_once(call)
        response = self._fast_local_tool_response(pending.user_input, call, tool_result)
        if response is None:
            response = self._completed_action_response(call)
        if response is None:
            response = self.llm.generate(self._tool_followup(base, call, tool_result))

        self._remember_episode("babyai", response)
        return response

    def reject_pending_tool(self) -> str:
        if self.tool_approvals is None or self.tool_approvals.load() is None:
            raise ToolProtocolError("No pending tool approval")
        self.tool_approvals.clear()
        response = "Доступ не предоставлен. Я не выполнял это действие."
        self._remember_episode("babyai", response)
        return response

    def think(self, user_input: str) -> str:
        return self._think(user_input).reply

    def think_stream(
        self,
        user_input: str,
        on_delta: Callable[[str], None],
        on_state: Callable[[str], None] | None = None,
    ) -> StreamReply:
        return self._think(user_input, on_delta=on_delta, on_state=on_state)

    def _think(
        self,
        user_input: str,
        *,
        on_delta: Callable[[str], None] | None = None,
        on_state: Callable[[str], None] | None = None,
    ) -> StreamReply:
        metrics = StreamMetrics()

        def finish(reply: str, *, gate: VisibleTextGate | None = None) -> StreamReply:
            if on_delta is not None:
                if gate is None:
                    if reply:
                        on_delta(reply)
                else:
                    delta = gate.finish(reply)
                    if delta:
                        on_delta(delta)
            return StreamReply(reply=reply, metrics=metrics)

        conversational_response = self._conversational_fast_response(user_input)
        if conversational_response is not None:
            self._remember_episode("user", user_input)
            self._remember_episode("babyai", conversational_response)
            return finish(conversational_response)

        if self.repair_tool_calls and self.agent is not None:
            direct_call = self.agent.infer_safe_local_intent(user_input)
            if direct_call is not None:
                base = self._base_prompt(user_input)
                response = self._execute_or_request_approval(
                    base,
                    user_input,
                    direct_call,
                    on_state=on_state,
                )
                self._remember_episode("user", user_input)
                self._remember_episode("babyai", response)
                return finish(response)

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

        gate: VisibleTextGate | None = None
        stream_authorized = False
        can_stream_answer = (
            on_delta is not None
            and (self.agent is None or not self.agent.requests_local_action(user_input))
            and (plan is None or plan.action is PlanAction.ANSWER)
        )
        generate_stream = getattr(self.llm, "generate_stream", None)
        if can_stream_answer and callable(generate_stream):
            visible_marker = new_visible_marker()
            gate = VisibleTextGate(
                marker=visible_marker,
                tool_names=() if self.agent is None else self.agent.tool_names()
            )

            def accept_candidate(candidate: str) -> None:
                assert gate is not None
                delta = gate.feed(candidate)
                if delta:
                    on_delta(delta)

            generation = generate_stream(
                with_visible_marker_contract(base, visible_marker),
                accept_candidate,
            )
            if gate.opened:
                body = gate.validated_open_body(generation.text)
                if body is None:
                    raise LLMError("Streaming response failed safety validation.")
                first = body
                stream_authorized = True
            else:
                try:
                    first = gate.strip_marker(generation.text)
                except StreamingSafetyError as exc:
                    raise LLMError("Streaming response failed safety validation.") from exc
            metrics = StreamMetrics(
                native_first_token_ms=getattr(generation, "first_token_ms", None),
                generation_ms=getattr(generation, "generation_ms", None),
                generated_tokens=getattr(generation, "generated_tokens", None),
                stop_reason=str(getattr(generation, "stop_reason", "completed")),
                model_calls=1,
            )
        else:
            first = self.llm.generate(base)
            metrics = StreamMetrics(model_calls=1, stop_reason="completed")
        response = first
        remember_response = True

        if (
            not stream_authorized
            and self.agent is not None
            and (plan is None or plan.action is PlanAction.TOOL)
        ):
            try:
                blocked_tool_call = False
                call = self.agent.parse_tool_call(first)
                if (
                    call is not None
                    and call.name in self.agent.tool_names()
                    and not self.agent.tool_compatible_with_intent(user_input, call.name)
                ):
                    response, remember_response = self._answer_without_internal_tool_json(user_input)
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
                    response = self._execute_or_request_approval(
                        base,
                        user_input,
                        call,
                        on_state=on_state,
                    )
                elif (
                    not blocked_tool_call
                    and self.repair_tool_calls
                    and self.agent.mentioned_tool(first) is not None
                ):
                    response = "Я понял, что для этого нужно локальное действие, но не смог безопасно подготовить его параметры."
            except (PermissionError, ToolProtocolError, FileNotFoundError, NotADirectoryError, ValueError) as exc:
                response = f"I could not use the requested tool: {exc}"

        self._remember_episode("user", user_input)
        if remember_response:
            self._remember_episode("babyai", response)
        return finish(response, gate=gate if response == first else None)

    def _remember_episode(self, role: str, content: str) -> None:
        (self.session_memory or self.memory).add(role, content, kind=MemoryKind.EPISODIC)
