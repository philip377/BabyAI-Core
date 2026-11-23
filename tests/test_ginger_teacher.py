import json
import pytest
from babyai.ginger_teacher import consult


class Model:
    def __init__(self, candidate="A"):
        self.candidate = candidate
        self.prompt = None

    def generate(self, prompt):
        self.prompt = prompt
        return json.dumps(dict(understanding="Урок поставщика", candidate=self.candidate,
                               explanation="Проверить объём"))


def payload():
    return dict(supplier="supplier", source="source", lesson="user explanation", candidates=["A", "B"])


def test_model_receives_lesson_and_observation():
    model = Model()
    assert consult(model, payload())["candidate"] == "A"
    assert "user explanation" in model.prompt
    assert "supplier" in model.prompt


@pytest.mark.parametrize("candidate", ["invented", 1, {}, []])
def test_unknown_or_invalid_candidate_rejected(candidate):
    with pytest.raises(ValueError):
        consult(Model(candidate), payload())


def test_ambiguity_and_duplicate_candidate():
    assert consult(Model(None), payload())["candidate"] is None
    with pytest.raises(ValueError):
        consult(Model(), payload() | {"candidates": ["A", "A"]})


@pytest.mark.parametrize("changes", [{"lesson": ""}, {"lesson": "x" * 4001},
                                      {"candidates": []}, {"source": None}])
def test_invalid_request_never_calls_model(changes):
    model = Model()
    with pytest.raises(ValueError):
        consult(model, payload() | changes)
    assert model.prompt is None


def test_malformed_model_reply_not_executed():
    class BadModel:
        def generate(self, prompt):
            return '{"tool":"click","candidate":"A"}'
    with pytest.raises(ValueError):
        consult(BadModel(), payload())


def test_desktop_command_uses_provider_without_chat_or_tools(tmp_path, monkeypatch):
    from babyai.config import BabyAIConfig
    from babyai.desktop_commands import DesktopCommands
    commands = DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider="echo"))
    monkeypatch.setattr(commands, "_provider", lambda: Model())
    monkeypatch.setattr(commands, "_core", lambda: pytest.fail("Must not enter agent runtime"))
    result = commands.execute("ginger.teach.preview", payload())
    assert result["ok"] is True
    assert result["candidate"] == "A"
    assert list(tmp_path.iterdir()) == []
