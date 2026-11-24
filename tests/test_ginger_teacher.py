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

def test_teacher_requests_bounded_native_budget(tmp_path, monkeypatch):
    from babyai.resident_native_brain import ResidentNativeBrainProvider, ResidentNativeStreamResult
    observed = []
    def generate(self, prompt, callback, *, max_tokens=None):
        observed.append(max_tokens)
        return ResidentNativeStreamResult(text=json.dumps(dict(understanding="rule", candidate="A", explanation="example")), generated_tokens=4, output_bytes=80, stop_reason="eog")
    monkeypatch.setattr(ResidentNativeBrainProvider, "generate_stream", generate)
    provider = ResidentNativeBrainProvider(model_path=tmp_path / "model", runtime_path=tmp_path / "runtime")
    assert consult(provider, payload())["candidate"] == "A"
    assert observed == [512]
    assert provider.max_tokens == 128

class ComparisonModel:
    def __init__(self, answers=None):
        self.prompts = []
        self.answers = answers

    def generate(self, prompt):
        self.prompts.append(prompt)
        attributes = dict.fromkeys(['identity', 'volume', 'concentration', 'tester', 'variant'], 'match')
        attributes.update((self.answers or [{}, {'volume': 'conflict'}])[len(self.prompts) - 1])
        return json.dumps(attributes)


def test_recognition_requires_no_supplier_lesson():
    model = ComparisonModel()
    request = payload()
    del request['lesson']
    assert consult(model, request, recognize=True)['candidate'] == 'A'
    assert len(model.prompts) == 2
    assert json.loads(model.prompts[0].split('\n')[-1])['candidate'] == 'A'
    assert json.loads(model.prompts[1].split('\n')[-1])['candidate'] == 'B'


def test_recognition_command_does_not_enter_agent_runtime(tmp_path, monkeypatch):
    from babyai.config import BabyAIConfig
    from babyai.desktop_commands import DesktopCommands
    commands = DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider='echo'))
    monkeypatch.setattr(commands, '_provider', lambda: ComparisonModel())
    monkeypatch.setattr(commands, '_core', lambda: pytest.fail('No tool runtime allowed'))
    request = payload()
    del request['lesson']
    assert commands.execute('ginger.recognize.preview', request)['candidate'] == 'A'
    assert list(tmp_path.iterdir()) == []


@pytest.mark.parametrize('answers', [[{}, {}], [{}, {'identity': 'unknown'}],
                                   [{'volume': 'conflict'}, {'tester': 'conflict'}]])
def test_recognition_refuses_ambiguity_or_no_match(answers):
    result = consult(ComparisonModel(answers), payload(), recognize=True)
    assert result['candidate'] is None
    assert len(result['comparisons']) == 2


def test_recognition_rejects_incomplete_comparison():
    with pytest.raises(ValueError):
        consult(Model(), payload(), recognize=True)


def test_recognition_bounds_work_without_truncating_candidates():
    model = ComparisonModel()
    with pytest.raises(ValueError):
        consult(model, payload() | {'candidates': [str(i) for i in range(11)]}, recognize=True)
    assert model.prompts == []


def test_recognition_does_not_accept_duplicate_matches():
    assert consult(ComparisonModel([{}, {}]), payload() | {'candidates': ['A', 'A']},
                   recognize=True)['candidate'] is None
    assert consult(ComparisonModel(), payload() | {'candidates': ['A', 'A']},
                   recognize=True)['candidate'] is None


@pytest.mark.parametrize('value', ['yes', None, {}, True])
def test_recognition_rejects_invalid_attribute(value):
    with pytest.raises(ValueError):
        consult(ComparisonModel([{'volume': value}]), payload(), recognize=True)
