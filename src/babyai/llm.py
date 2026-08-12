from abc import ABC, abstractmethod


class LLMProvider(ABC):
    @abstractmethod
    def generate(self, prompt: str) -> str:
        raise NotImplementedError


class EchoProvider(LLMProvider):
    def generate(self, prompt: str) -> str:
        return f"[echo] {prompt}"
