from dataclasses import dataclass

from .llm import LLMProvider
from .memory import MemoryStore


@dataclass(slots=True)
class Primus:
    llm: LLMProvider
    memory: MemoryStore

    def think(self, user_input: str) -> str:
        context = "\n".join(item.content for item in self.memory.recent())
        prompt = f"Memory:\n{context}\n\nUser:\n{user_input}" if context else user_input
        response = self.llm.generate(prompt)
        self.memory.add(f"USER: {user_input}")
        self.memory.add(f"BABYAI: {response}")
        return response
