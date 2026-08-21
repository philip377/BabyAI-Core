from babyai.agent import AgentExecutor
from babyai.identity import Identity
from babyai.llm import LLMProvider
from babyai.memory import SQLiteMemoryStore
from babyai.permissions import PermissionStore
from babyai.primus import Primus


class PolicyAwareProvider(LLMProvider):
    def __init__(self) -> None:
        self.prompts: list[str] = []

    def generate(self, prompt: str) -> str:
        self.prompts.append(prompt)
        assert "Respond in the language of the user's latest message." in prompt
        assert "unless the user explicitly asks for a translation" in prompt
        return "Я могу рассказывать на разные темы и помогать с вопросами."


def test_regular_russian_answer_uses_response_language_policy_without_tools(tmp_path):
    provider = PolicyAwareProvider()
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(PermissionStore(tmp_path / "permissions.json")),
    )

    reply = primus.think("Расскажи, с чем ты можешь помочь")

    assert reply == "Я могу рассказывать на разные темы и помогать с вопросами."
    assert "(" not in reply
    assert "Available tools:" not in provider.prompts[0]
