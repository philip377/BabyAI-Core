import json

import pytest

from babyai.curiosa import Curiosa, CuriosaProtocolError, CuriosityStore
from babyai.llm import EchoProvider


class StaticProvider:
    def __init__(self, payload: dict[str, str]) -> None:
        self.payload = payload

    def generate(self, prompt: str) -> str:
        return json.dumps(self.payload)


def test_curiosa_parses_single_question() -> None:
    item = Curiosa(StaticProvider({"question": "Which port is open?", "reason": "It determines reachability."})).propose("Connection failed")
    assert item.question == "Which port is open?"
    assert "reachability" in item.reason


def test_curiosa_rejects_extra_fields() -> None:
    with pytest.raises(CuriosaProtocolError):
        Curiosa.parse(json.dumps({"question": "Q?", "reason": "R", "search": "now"}))


def test_curiosity_store_is_separate_state(tmp_path) -> None:
    path = tmp_path / "curiosity.json"
    store = CuriosityStore(path)
    item = Curiosa(StaticProvider({"question": "Q?", "reason": "R"})).propose("context")
    store.save(item)
    assert store.load() is not None
    store.clear()
    assert store.load() is None


def test_echo_provider_fails_protocol_instead_of_side_effecting() -> None:
    with pytest.raises(CuriosaProtocolError):
        Curiosa(EchoProvider()).propose("context")
