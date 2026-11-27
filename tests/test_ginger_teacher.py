import json
import pytest
from babyai.ginger_teacher import consult


@pytest.fixture(autouse=True)
def isolate_identity_review(monkeypatch):
    # These tests exercise attribute aggregation and the desktop bridge.
    # Grounding and extraction are tested in test_ginger_identity_evidence.
    monkeypatch.setattr('babyai.ginger_teacher.review_identity', lambda provider, source, candidates:
        dict(source=source, candidates=[dict(candidate=c, identity='match') for c in candidates], can_match=False))
    monkeypatch.setattr('babyai.ginger_teacher.review_volume', lambda source, candidates:
        dict(source=source, candidates=[dict(candidate=c, volume='match') for c in candidates], can_match=False))


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


@pytest.mark.parametrize('narrow,original,expected', [
    ('conflict', 'match', 'conflict'), ('unknown', 'match', 'unknown'),
    ('match', 'unknown', 'unknown'), ('match', 'conflict', 'conflict'),
    ('unknown', 'conflict', 'conflict'),
])
def test_identity_review_never_promotes_uncertain_or_conflicting_attributes(monkeypatch, narrow, original, expected):
    evidence = dict(source='source', candidates=[dict(candidate='A', identity=narrow)], can_match=False)
    monkeypatch.setattr('babyai.ginger_teacher.review_identity', lambda *_: evidence)
    result = consult(ComparisonModel([{'identity': original}]), payload() | {'candidates': ['A']}, recognize=True)
    assert result['candidate'] is None
    assert result['comparisons'][0]['attributes']['identity'] == expected
    assert result['identity_evidence'] == evidence


def test_full_name_conflict_excludes_only_that_candidate(monkeypatch):
    monkeypatch.setattr('babyai.ginger_teacher.review_identity', lambda *_:
        dict(candidates=[dict(identity='match'), dict(identity='conflict')], can_match=False))
    result = consult(ComparisonModel([{}, {}]), payload(), recognize=True)
    assert result['candidate'] == 'A'
    assert result['comparisons'][1]['attributes']['identity'] == 'conflict'


@pytest.mark.parametrize('source,candidates,model_answers,selected,volumes', [
    ('Acme Bloom EDP 100ml', ['Acme BLOOM EDP 100ml', 'Acme BLOOM EDP 50ml'],
     [{}, {}], 'Acme BLOOM EDP 100ml', ['match', 'conflict']),
    ('Acme Bloom EDP 2x30ml', ['Acme BLOOM EDP 60ml'], [{}], None, ['unknown']),
    ('Acme Bloom EDP', ['Acme BLOOM EDP 100ml'], [{}], None, ['unknown']),
    ('Acme Bloom EDP 100ml', ['Acme BLOOM EDP 100ml'], [{'volume': 'unknown'}], None, ['unknown']),
    ('Acme Bloom EDP 100ml', ['Acme BLOOM EDP 100ml'], [{'volume': 'conflict'}], None, ['conflict']),
    ('Acme Bloom EDP 100ml', ['Acme BLOOM EDP 100ml', 'Acme BLOOM EDP'],
     [{}, {}], None, ['match', 'unknown']),
    ('Acme Bloom EDP 100ml', ['Acme BLOOM EDP 100ml', 'Acme BLOOM EDP'],
     [{}, {'volume': 'conflict'}], None, ['match', 'unknown']),
    ('Acme Bloom EDP 2x30ml', ['Acme BLOOM EDP 60ml'], [{'volume': 'conflict'}], None, ['unknown']),
])
def test_explicit_volume_guards_model_proposals(monkeypatch, source, candidates, model_answers, selected, volumes):
    from babyai.ginger_volume_evidence import review_volume
    monkeypatch.setattr('babyai.ginger_teacher.review_volume', review_volume)
    result = consult(ComparisonModel(model_answers), payload() | dict(source=source, candidates=candidates), recognize=True)
    assert result['candidate'] == selected
    assert [c['attributes']['volume'] for c in result['comparisons']] == volumes
    assert result['volume_evidence']['can_match'] is False


def test_volume_review_does_not_change_confirmed_lesson_path(monkeypatch):
    monkeypatch.setattr('babyai.ginger_teacher.review_volume', lambda *_: pytest.fail('Lesson path unchanged'))
    assert consult(Model(), payload())['candidate'] == 'A'
