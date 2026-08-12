from babyai.identity import Identity
from babyai.llm import EchoProvider
from babyai.memory import SQLiteMemoryStore
from babyai.primus import Primus


def test_primus_persists_exchange(tmp_path) -> None:
    db_path = tmp_path / "memory.sqlite3"
    memory = SQLiteMemoryStore(db_path)
    core = Primus(
        llm=EchoProvider(),
        memory=memory,
        identity=Identity(name="BabyAI", owner="tester"),
    )

    result = core.think("hello")

    assert "USER: hello" in result
    assert len(memory.recent()) == 2

    reopened = SQLiteMemoryStore(db_path)
    records = reopened.recent()
    assert [record.role for record in records] == ["user", "babyai"]
    assert records[0].content == "hello"
