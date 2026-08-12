from dataclasses import dataclass

from .identity import Identity
from .llm import LLMProvider
from .memory import MemoryKind, MemoryStore


@dataclass(slots=True)
class Primus:
    llm: LLMProvider
    memory: MemoryStore
    identity: Identity

    def think(self, user_input: str) -> str:
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
        prompt_parts.append(f"USER: {user_input}")

        response = self.llm.generate("\n\n".join(prompt_parts))
        self.memory.add("user", user_input, kind=MemoryKind.EPISODIC)
        self.memory.add("babyai", response, kind=MemoryKind.EPISODIC)
        return response
