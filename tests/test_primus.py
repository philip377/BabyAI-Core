from babyai.llm import EchoProvider
from babyai.memory import InMemoryStore
from babyai.primus import Primus


def test_primus_remembers_exchange() -> None:
    memory = InMemoryStore()
    core = Primus(llm=EchoProvider(), memory=memory)
    result = core.think("hello")
    assert result == "[echo] hello"
    assert len(memory.recent()) == 2
