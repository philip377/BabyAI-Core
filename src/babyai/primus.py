from dataclasses import dataclass

from .identity import Identity
from .llm import LLMProvider
from .memory import MemoryStore


@dataclass(slots=True)
class Primus:
    llm: LLMProvider
    memory: MemoryStore
    identity: Identity

    def think(self, user_input: str) -> str:
        history = "\n".join(
            f"{item.role.upper()}: {item.content}" for item in self.memory.recent(limit=12)
        )
        prompt_parts = [self.identity.system_context()]
        if history:
            prompt_parts.append(f"Recent memory:\n{history}")
        prompt_parts.append(f"USER: {user_input}")
        response = self.llm.generate("\n\n".join(prompt_parts))
        self.memory.add("user", user_input)
        self.memory.add("babyai", response)
        return response
