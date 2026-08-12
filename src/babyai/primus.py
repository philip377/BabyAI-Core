from dataclasses import dataclass

from .agent import AgentExecutor, ToolProtocolError
from .identity import Identity
from .llm import LLMProvider
from .memory import MemoryKind, MemoryStore
from .planner import PlanAction, Planner, PlannerProtocolError


@dataclass(slots=True)
class Primus:
    llm: LLMProvider
    memory: MemoryStore
    identity: Identity
    agent: AgentExecutor | None = None
    planner: Planner | None = None
    max_context_chars: int = 12_000

    def _base_prompt(self, user_input: str) -> str:
        prompt_parts = [self.identity.system_context()]
        if self.agent is not None:
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
        all_parts.extend(memory_parts)
        if self.agent is not None:
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

    def think(self, user_input: str) -> str:
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
                call = self.agent.parse_tool_call(first)
                if call is not None:
                    tool_result = self.agent.execute(call)
                    followup = (
                        base
                        + "\n\nThe tool call was executed successfully.\n"
                        + f"TOOL: {call.name}\nRESULT:\n{tool_result}\n\n"
                        + "Answer the user using the tool result. Do not emit another tool call."
                    )
                    response = self.llm.generate(followup)
            except (PermissionError, ToolProtocolError, FileNotFoundError, NotADirectoryError, ValueError) as exc:
                response = f"I could not use the requested tool: {exc}"

        self.memory.add("user", user_input, kind=MemoryKind.EPISODIC)
        self.memory.add("babyai", response, kind=MemoryKind.EPISODIC)
        return response
