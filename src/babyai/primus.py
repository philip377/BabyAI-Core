from dataclasses import dataclass

from .agent import AgentExecutor, ToolProtocolError
from .identity import Identity
from .llm import LLMProvider
from .memory import MemoryKind, MemoryStore


@dataclass(slots=True)
class Primus:
    llm: LLMProvider
    memory: MemoryStore
    identity: Identity
    agent: AgentExecutor | None = None

    def _base_prompt(self, user_input: str) -> str:
        episodic = "\n".join(
            f"{item.role.upper()}: {item.content}"
            for item in self.memory.recent(limit=12, kind=MemoryKind.EPISODIC)
        )
        facts = "\n".join(
            f"- {item.content}"
            for item in self.memory.recent(limit=8, kind=MemoryKind.FACT)
        )
        knowledge = "\n".join(
            f"- {item.content}"
            for item in self.memory.recent(limit=6, kind=MemoryKind.KNOWLEDGE)
        )

        prompt_parts = [self.identity.system_context()]
        if facts:
            prompt_parts.append(f"Known facts:\n{facts}")
        if knowledge:
            prompt_parts.append(f"Relevant learned knowledge:\n{knowledge}")
        if episodic:
            prompt_parts.append(f"Recent episodic memory:\n{episodic}")
        if self.agent is not None:
            prompt_parts.append(self.agent.catalog())
        prompt_parts.append(f"USER: {user_input}")
        return "\n\n".join(prompt_parts)

    def think(self, user_input: str) -> str:
        first = self.llm.generate(self._base_prompt(user_input))
        response = first

        if self.agent is not None:
            try:
                call = self.agent.parse_tool_call(first)
                if call is not None:
                    tool_result = self.agent.execute(call)
                    followup = (
                        self._base_prompt(user_input)
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
