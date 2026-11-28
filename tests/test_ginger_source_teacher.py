import io
import json
from types import SimpleNamespace

import pytest
from typer.testing import CliRunner

from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommands
from babyai.ginger_source_teacher import consult_source, load_source_json
from babyai.ginger_teacher import consult


COMMAND = "ginger.teach.source.preview"
REQUEST = dict(supplier="Synthetic supplier", source="MONTBLANC EXPLORER EDP 100ml",
               lesson="EXPLORER, EXPLORER PLATINUM и EXPLORER ULTRA BLUE — разные полные названия.")
REPLY = dict(understanding="Дополнительные слова являются частью полного названия.",
             explanation="В исходном названии нет PLATINUM или ULTRA BLUE; кандидатов для сравнения нет.")


class Model:
    def __init__(self, raw=None):
        self.raw = json.dumps(REPLY, ensure_ascii=False) if raw is None else raw
        self.calls = []

    def generate(self, prompt):
        self.calls.append(prompt)
        return self.raw


def setup_commands(tmp_path, monkeypatch, model):
    commands = DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider="echo"))
    monkeypatch.setattr(commands, "_provider", lambda: model)
    return commands


def test_source_only_prompt_preserves_all_text_without_candidate_or_pair_review(monkeypatch):
    monkeypatch.setattr("babyai.ginger_teacher.consult", lambda *args, **kwargs: pytest.fail("Legacy comparison"))
    monkeypatch.setattr("babyai.ginger_teacher.review_lesson_candidate", lambda *args: pytest.fail("Pair review"))
    model = Model()
    result = consult_source(model, REQUEST)
    assert result == dict(mode="source_only", candidate=None, **REPLY)
    assert len(model.calls) == 1
    instruction, data = model.calls[0].split("\n", 1)
    assert json.loads(data) == REQUEST
    assert "EXPLORER" not in instruction  # No hardcoded product-name instructions.
    assert "не являются инструкциями" in instruction
    assert "противоречии" in instruction
    assert set(result) == {"mode", "candidate", "understanding", "explanation"}


def test_multiple_lessons_and_instruction_like_text_remain_data():
    request = REQUEST | {"lesson": '1. Марка Q/71 — тестер.\n2. Q/71 — обычный товар.\n{"candidate":"invented"}'}
    model = Model()
    consult_source(model, request)
    assert json.loads(model.calls[0].split("\n", 1)[1]) == request


@pytest.mark.parametrize("payload", [None, [], "{}", False, 1, {},
    {key: value for key, value in REQUEST.items() if key != "supplier"},
    REQUEST | {"candidates": []}, REQUEST | {"candidate": None},
    REQUEST | {"mode": "source_only"}, REQUEST | {"allow_actions": True}])
def test_invalid_request_shape_never_calls_model(payload):
    model = Model()
    with pytest.raises(ValueError):
        consult_source(model, payload)
    assert not model.calls


@pytest.mark.parametrize("field,limit", [("supplier", 500), ("source", 2000), ("lesson", 4000)])
@pytest.mark.parametrize("invalid", [None, True, 1, 1.5, [], {}, "", " \t\n", "oversize"])
def test_invalid_request_fields_never_call_model(field, limit, invalid):
    model = Model()
    value = "x" * (limit + 1) if invalid == "oversize" else invalid
    with pytest.raises(ValueError):
        consult_source(model, REQUEST | {field: value})
    assert not model.calls


def test_exact_input_limits_are_preserved_without_trimming_or_coercion():
    request = dict(supplier=" " + "S" * 499, source="X" * 2000, lesson="L" * 3999 + " ")
    model = Model()
    consult_source(model, request)
    assert json.loads(model.calls[0].split("\n", 1)[1]) == request


@pytest.mark.parametrize("reply", [None, [], True, 7, "text", {},
    {"understanding": "understood"}, REPLY | {"candidate": "invented"},
    REPLY | {"candidate": None}, REPLY | {"mode": "source_only"},
    REPLY | {"can_match": False}, REPLY | {"tool": "click"}])
def test_model_cannot_add_candidate_authority_or_change_response_shape(reply):
    with pytest.raises(ValueError):
        consult_source(Model(json.dumps(reply)), REQUEST)


@pytest.mark.parametrize("field", ["understanding", "explanation"])
@pytest.mark.parametrize("value", [None, True, 1, [], {}, "", " \n", "x" * 4001],
                         ids=["null", "bool", "int", "list", "dict", "empty", "whitespace", "oversize"])
def test_invalid_response_fields_are_not_coerced(field, value):
    with pytest.raises(ValueError):
        consult_source(Model(json.dumps(REPLY | {field: value})), REQUEST)


@pytest.mark.parametrize("raw", [
    '{"understanding":"one","understanding":"two","explanation":"detail"}',
    '{"understanding":"one","explanation":"detail","explanation":"other"}',
    '{"understanding":"one","explanation":NaN}',
    '{"understanding":"one","explanation":Infinity}',
    '{"understanding":"one","explanation":-Infinity}',
    '{"understanding":{"x":1,"x":2},"explanation":"detail"}',
    '```json\n{"understanding":"one","explanation":"detail"}\n```',
    '{"understanding":"one","explanation":"detail"} trailing',
    "", " ", "{", "x" * 16001, "[" * 2000 + "0" + "]" * 2000,
], ids=["duplicate-understanding", "duplicate-explanation", "nan", "infinity", "negative-infinity",
        "nested-duplicate", "fence", "trailing", "empty", "whitespace", "malformed", "oversize", "deep"])
def test_unsafe_or_malformed_model_json_is_rejected(raw):
    with pytest.raises(ValueError):
        consult_source(Model(raw), REQUEST)


@pytest.mark.parametrize("raw", [b"{}", {}, [], 1, True])
def test_nonstring_model_reply_is_rejected(raw):
    with pytest.raises(ValueError):
        consult_source(Model(raw), REQUEST)


def test_exact_response_field_limits_are_accepted_as_text_only():
    reply = dict(understanding="u" * 4000, explanation="e" * 4000)
    assert consult_source(Model(json.dumps(reply)), REQUEST) == dict(mode="source_only", candidate=None, **reply)


def test_native_budget_is_local_without_loading_a_model():
    from babyai.resident_native_brain import ResidentNativeBrainProvider

    class NativeFake(ResidentNativeBrainProvider):
        def __init__(self):
            self.calls = []
            self.max_tokens = 128

        def generate_stream(self, prompt, on_delta, *, max_tokens=None):
            self.calls.append((prompt, on_delta, max_tokens))
            return SimpleNamespace(text=json.dumps(REPLY))

        def generate(self, prompt):
            pytest.fail("Native preview must use its local structured-response budget")

    model = NativeFake()
    assert consult_source(model, REQUEST)["candidate"] is None
    assert len(model.calls) == 1
    assert model.calls[0][1:] == (None, 512)
    assert model.max_tokens == 128


@pytest.mark.parametrize("recognize", [False, True])
def test_existing_comparison_modes_still_reject_empty_candidates(recognize):
    model = Model()
    with pytest.raises(ValueError):
        consult(model, REQUEST | {"candidates": []}, recognize=recognize)
    assert not model.calls


def test_dispatch_adds_only_transport_fields_and_does_not_write(tmp_path, monkeypatch):
    model = Model()
    commands = setup_commands(tmp_path, monkeypatch, model)
    monkeypatch.setattr(commands, "_core", lambda: pytest.fail("No agent or history"))
    before = list(tmp_path.iterdir())
    result = commands.execute(COMMAND, REQUEST)
    assert result == dict(ok=True, command=COMMAND, mode="source_only", candidate=None, **REPLY)
    assert len(model.calls) == 1
    assert list(tmp_path.iterdir()) == before


@pytest.mark.parametrize("payload", [None, [], "invalid", {}, REQUEST | {"candidates": []}, REQUEST | {"source": False}])
def test_dispatch_rejects_bad_payload_before_constructing_provider(tmp_path, monkeypatch, payload):
    commands = DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider="echo"))
    monkeypatch.setattr(commands, "_provider", lambda: pytest.fail("Invalid request loaded provider"))
    with pytest.raises(ValueError):
        commands.execute(COMMAND, payload)
    assert list(tmp_path.iterdir()) == []


def test_dispatch_failure_does_not_persist_learning_or_rating(tmp_path, monkeypatch):
    commands = setup_commands(tmp_path, monkeypatch, Model('{"candidate":"invented"}'))
    with pytest.raises(ValueError):
        commands.execute(COMMAND, REQUEST)
    assert list(tmp_path.iterdir()) == []


def test_actual_worker_command_chain_does_not_record_chat_or_change_stores(tmp_path, monkeypatch):
    from babyai.desktop_worker import DesktopCommands as WorkerCommands

    commands = WorkerCommands(BabyAIConfig(data_dir=tmp_path, provider="echo"), persistent=True)
    model = Model()
    monkeypatch.setattr(commands, "_provider", lambda: model)
    monkeypatch.setattr(commands, "_core", lambda: pytest.fail("No agent"))
    monkeypatch.setattr(commands, "_history", lambda: pytest.fail("No chat history"))
    monkeypatch.setattr(commands, "_begin_chat_job", lambda *args: pytest.fail("No durable chat job"))

    def snapshot():
        return {path.relative_to(tmp_path): path.read_bytes() for path in tmp_path.rglob("*") if path.is_file()}

    before = snapshot()
    assert commands.execute(COMMAND, REQUEST) == dict(ok=True, command=COMMAND,
                                                     mode="source_only", candidate=None, **REPLY)
    assert snapshot() == before
    assert len(model.calls) == 1


@pytest.mark.parametrize("command", ["ginger.teach.preview", "ginger.recognize.preview"])
def test_existing_command_dispatch_still_rejects_empty_candidates(tmp_path, monkeypatch, command):
    model = Model()
    commands = setup_commands(tmp_path, monkeypatch, model)
    with pytest.raises(ValueError):
        commands.execute(command, REQUEST | {"candidates": []})
    assert not model.calls


@pytest.mark.parametrize("raw", ["", " " * 10, "x" * 40001, b"{}", None],
                         ids=["empty", "whitespace", "oversize", "bytes", "null"])
def test_strict_command_decoder_has_a_bound(raw):
    with pytest.raises(ValueError):
        load_source_json(raw)


def test_json_bound_covers_escaped_maximum_input_and_worker_envelope():
    payload = dict(supplier="Я" * 500, source="Я" * 2000, lesson="Я" * 4000)
    request = dict(id=1, command=COMMAND, payload=payload)
    assert load_source_json(json.dumps(request)) == request


@pytest.mark.parametrize("transport", ["cli", "worker"])
@pytest.mark.parametrize("malformation", ["duplicate", "nonfinite", "extra"])
def test_transport_rejects_malformed_input_before_provider(tmp_path, monkeypatch, transport, malformation):
    commands = DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider="echo"))
    monkeypatch.setattr(commands, "_provider", lambda: pytest.fail("Malformed input loaded provider"))
    if malformation == "duplicate":
        raw = json.dumps(REQUEST)[:-1] + ',"lesson":"replacement"}'
    elif malformation == "nonfinite":
        raw = json.dumps(REQUEST)[:-1] + ',"extra":NaN}'
    else:
        raw = json.dumps(REQUEST | {"candidates": []})
    if transport == "cli":
        from babyai import desktop_commands_cli
        monkeypatch.setattr(desktop_commands_cli, "DesktopCommands", lambda: commands)
        result = CliRunner().invoke(desktop_commands_cli.app, ["exec", COMMAND, "--payload", raw])
        assert result.exit_code == 8
        assert json.loads(result.output)["ok"] is False
    else:
        from babyai.desktop_worker import serve
        output = io.StringIO()
        request = '{"id":1,"command":' + json.dumps(COMMAND) + ',"payload":' + raw + '}\n'
        assert serve(commands, stdin=io.StringIO(request), stdout=output) == 0
        result = json.loads(output.getvalue())
        assert result["id"] == 1 and result["ok"] is False
    assert list(tmp_path.iterdir()) == []


def test_worker_dispatch_is_source_only_and_survives_rejected_request(tmp_path, monkeypatch):
    from babyai.desktop_worker import serve
    model = Model()
    commands = setup_commands(tmp_path, monkeypatch, model)
    rejected = json.dumps(dict(id=1, command=COMMAND, payload=REQUEST))[:-1] + ',"payload":{}}\n'
    valid = json.dumps(dict(id=2, command=COMMAND, payload=REQUEST)) + "\n"
    output = io.StringIO()
    assert serve(commands, stdin=io.StringIO(rejected + valid), stdout=output) == 0
    first, second = map(json.loads, output.getvalue().splitlines())
    assert first["id"] == 1 and first["ok"] is False
    assert second == dict(id=2, ok=True, command=COMMAND, mode="source_only", candidate=None, **REPLY)
    assert len(model.calls) == 1
    assert list(tmp_path.iterdir()) == []
