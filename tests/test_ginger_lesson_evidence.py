import json

import pytest

from babyai.ginger_lesson_evidence import review_lesson_candidate


FIELDS = ("identity", "volume", "concentration", "tester", "variant")
SUPPLIER = "Поставщик"
SOURCE = "Acme Bloom EDP 50ml K7"
CANDIDATE = "Acme BLOOM edp 50ml tester"
LESSON = "1. K7 означает тестер.\n\n2. Регистр не меняет название аромата."


@pytest.fixture(autouse=True)
def grounded_identity_match(monkeypatch):
    # Literal extraction is exercised separately and in the native-budget
    # integration below. Most tests isolate field parsing and aggregation.
    monkeypatch.setattr("babyai.ginger_lesson_evidence.review_identity", lambda provider, source, candidates, *, lesson:
        dict(source=source, candidates=[dict(candidate=candidates[0], identity="match")], can_match=False))
    monkeypatch.setattr("babyai.ginger_lesson_evidence.review_markings", lambda *args, **kwargs:
        marking_result())


def marking_result(**updates):
    return dict(attributes=dict(concentration="match", tester="match") | updates, can_match=False)


def values(**updates):
    return dict.fromkeys(FIELDS, "match") | updates


class Model:
    def __init__(self, attributes=None, replies=None):
        self.attributes = values() if attributes is None else attributes
        self.replies = {} if replies is None else replies
        self.calls = []

    def generate(self, prompt):
        self.calls.append(prompt)
        field = json.loads(prompt.split("\n")[-1])["attribute"]
        return self.replies[field] if field in self.replies else json.dumps(dict(status=self.attributes[field]))


def review(model, **updates):
    return review_lesson_candidate(model, **(dict(supplier=SUPPLIER, source=SOURCE,
                                                lesson=LESSON, candidate=CANDIDATE) | updates))


def test_each_focused_comparison_receives_pair_and_all_lessons_unchanged_without_side_effects(tmp_path, monkeypatch):
    monkeypatch.chdir(tmp_path)
    model = Model()
    source = SOURCE + '\n"Служебная строка": не менять формат'
    lesson = " " + LESSON + "\n3. Дополнительное объяснение. "
    result = review(model, source=source, lesson=lesson)
    assert [json.loads(prompt.split("\n")[-1]) for prompt in model.calls] == [
        dict(attribute=field, supplier=SUPPLIER, source=source, lesson=lesson, candidate=CANDIDATE)
        for field in FIELDS]
    assert result == dict(attributes=values(), can_match=False, identity_evidence=dict(
        source=source, candidates=[dict(candidate=CANDIDATE, identity="match")], can_match=False),
        marking_evidence=marking_result())
    assert "candidate" not in result and "tools" not in result
    assert list(tmp_path.iterdir()) == []


@pytest.mark.parametrize("field", FIELDS)
@pytest.mark.parametrize("verdict", ["unknown", "conflict"])
def test_material_uncertainty_and_conflict_are_not_promoted_or_mixed_with_other_fields(field, verdict):
    attributes = values(**{field: verdict})
    model = Model(attributes)
    result = review(model)
    assert result["attributes"] == attributes
    assert result["can_match"] is False
    assert len(model.calls) == 5
    # Later checks see only the original evidence, not earlier model verdicts.
    assert all(set(json.loads(prompt.split("\n")[-1])) == {
        "attribute", "supplier", "source", "lesson", "candidate"} for prompt in model.calls)


@pytest.mark.parametrize("grounded", ["unknown", "conflict"])
def test_grounded_full_fragrance_can_reject_field_match_and_retains_evidence(monkeypatch, grounded):
    checked = []
    evidence = dict(source=SOURCE, source_fields=dict(brand="Acme", fragrance="Bloom"),
                    candidates=[dict(candidate=CANDIDATE, identity=grounded)], can_match=False)

    def check(provider, source, candidates, *, lesson):
        checked.append((provider, source, candidates, lesson))
        return evidence

    monkeypatch.setattr("babyai.ginger_lesson_evidence.review_identity", check)
    model = Model()
    result = review(model)
    assert checked == [(model, SOURCE, [CANDIDATE], LESSON)]
    assert result["attributes"] == values(identity=grounded)
    assert result["identity_evidence"] == evidence
    assert result["can_match"] is False and "candidate" not in result


@pytest.mark.parametrize("identity", ["unknown", "conflict"])
def test_grounded_review_never_runs_to_promote_an_existing_identity_nonmatch(monkeypatch, identity):
    def forbidden(*_):
        raise AssertionError("Grounded review must not try to rescue an identity nonmatch")

    monkeypatch.setattr("babyai.ginger_lesson_evidence.review_identity", forbidden)
    result = review(Model(values(identity=identity)))
    assert result == dict(attributes=values(identity=identity), can_match=False)


def test_failed_grounded_review_stops_proposal_without_fallback(monkeypatch):
    checked = []

    def failed(provider, source, candidates, *, lesson):
        checked.append((source, candidates))
        raise ValueError("Непроверяемые фрагменты названия")

    monkeypatch.setattr("babyai.ginger_lesson_evidence.review_identity", failed)
    model = Model()
    with pytest.raises(ValueError, match="Непроверяемые"):
        review(model)
    assert checked == [(SOURCE, [CANDIDATE])]
    assert len(model.calls) == 5


@pytest.mark.parametrize("field", ["concentration", "tester"])
@pytest.mark.parametrize("status", ["unknown", "conflict"])
def test_separately_extracted_markings_veto_unanimous_pair_checks(monkeypatch, field, status):
    checked = []
    evidence = marking_result(**{field: status})

    def check(provider, source, candidate, *, lesson):
        checked.append((provider, source, candidate, lesson))
        return evidence

    monkeypatch.setattr("babyai.ginger_lesson_evidence.review_markings", check)
    model = Model()
    result = review(model)
    assert checked == [(model, SOURCE, CANDIDATE, LESSON)]
    assert result["attributes"] == values(**{field: status})
    assert result["marking_evidence"] == evidence
    assert result["can_match"] is False and "candidate" not in result


@pytest.mark.parametrize("field", FIELDS)
@pytest.mark.parametrize("status", ["unknown", "conflict"])
def test_marking_extraction_never_runs_to_rescue_prior_nonmatch(monkeypatch, field, status):
    monkeypatch.setattr("babyai.ginger_lesson_evidence.review_markings",
                        lambda *args, **kwargs: pytest.fail("No rescue of a rejected proposal"))
    result = review(Model(values(**{field: status})))
    assert result["attributes"] == values(**{field: status})
    assert "marking_evidence" not in result


def test_failed_marking_extraction_stops_proposal_without_fallback(monkeypatch):
    def failed(*args, **kwargs):
        raise ValueError("Непроверяемые обозначения")

    monkeypatch.setattr("babyai.ginger_lesson_evidence.review_markings", failed)
    with pytest.raises(ValueError, match="Непроверяемые обозначения"):
        review(Model())


@pytest.mark.parametrize("source,candidate,model_volume,expected", [
    ("Acme Bloom edp 100ml", "Acme BLOOM edp 50ml", "match", "conflict"),
    ("Acme Bloom edp 100ml", "Acme BLOOM edp 50ml", "unknown", "conflict"),
    ("Acme Bloom edp 0,05l", "Acme BLOOM edp 50ml", "unknown", "unknown"),
    ("Acme Bloom edp 0,05l", "Acme BLOOM edp 50ml", "conflict", "conflict"),
    ("Acme Bloom edp 0,05l", "Acme BLOOM edp 50ml", "match", "match"),
    ("Acme Bloom edp", "Acme BLOOM edp 50ml", "match", "unknown"),
    ("Acme Bloom edp 2x30ml", "Acme BLOOM edp 60ml", "match", "unknown"),
    ("Acme Bloom edp 2x30ml", "Acme BLOOM edp 60ml", "conflict", "conflict"),
    # A digit inside an alphanumeric marking is not a standalone quantity.
    # The literal 100ml remains checked; a separate number stays ambiguous.
    ("Acme Bloom Z9 K4 100ml", "Acme BLOOM edp 100ml tester", "match", "match"),
    ("Acme Bloom Z9 K4 100ml", "Acme BLOOM edp 50ml tester", "match", "conflict"),
    ("Acme Bloom Z9 K4 100ml", "Acme BLOOM edp 100ml tester", "unknown", "unknown"),
    ("Acme Bloom 4 100ml", "Acme BLOOM edp 100ml tester", "match", "unknown"),
])
def test_explicit_volume_guard_only_restricts_model_proposal(source, candidate, model_volume, expected):
    result = review(Model(values(volume=model_volume)), source=source, candidate=candidate)
    assert result["attributes"] == values(volume=expected)
    assert result["can_match"] is False


@pytest.mark.parametrize("reply", [
    "not json", "```json\n{}\n```", "[]", "null", '\"match\"', "{}",
    json.dumps({"status": "match", "candidate": CANDIDATE}),
    json.dumps({"tester": "match"}), json.dumps({"status": True}),
    json.dumps({"status": ["match"]}), json.dumps({"status": "yes"}),
    json.dumps({"status": None}), json.dumps(values()),
    '{"status":"conflict","status":"match"}',
    '{"status":"match","status":"match"}', '{"status":NaN}',
    " " * 4001, {"status": "match"}, None,
])
def test_bad_field_reply_stops_review_without_partial_positive_result_or_retry(reply):
    model = Model(replies={"tester": reply})
    with pytest.raises(ValueError):
        review(model)
    assert [json.loads(prompt.split("\n")[-1])["attribute"] for prompt in model.calls] == list(FIELDS[:4])


@pytest.mark.parametrize("field,limit", [("supplier", 500), ("source", 2000),
                                         ("lesson", 4000), ("candidate", 2000)])
@pytest.mark.parametrize("bad", ["", "  ", None, False, [], "over_limit"])
def test_invalid_or_unbounded_input_never_reaches_model(field, limit, bad):
    model = Model()
    value = "x" * (limit + 1) if bad == "over_limit" else bad
    with pytest.raises(ValueError):
        review(model, **{field: value})
    assert model.calls == []


def test_native_verification_uses_five_small_local_budgets_and_no_tools(tmp_path, monkeypatch):
    from babyai.resident_native_brain import ResidentNativeBrainProvider, ResidentNativeStreamResult

    observed = []

    def generate(self, prompt, callback, *, max_tokens=None):
        observed.append((json.loads(prompt.split("\n")[-1]), callback, max_tokens))
        return ResidentNativeStreamResult(text=json.dumps(dict(status="match")), generated_tokens=10,
                                          output_bytes=20, stop_reason="eog")

    monkeypatch.setattr(ResidentNativeBrainProvider, "generate_stream", generate)
    provider = ResidentNativeBrainProvider(model_path=tmp_path / "model", runtime_path=tmp_path / "runtime")
    assert review(provider)["can_match"] is False
    assert observed == [(dict(attribute=field, supplier=SUPPLIER, source=SOURCE, lesson=LESSON,
                             candidate=CANDIDATE), None, 96) for field in FIELDS]
    assert provider.max_tokens == 128
    assert list(tmp_path.iterdir()) == []


def test_native_grounded_fragrance_check_has_bounded_extra_calls_and_blocks_flanker(tmp_path, monkeypatch):
    from babyai.ginger_identity_evidence import review_identity
    from babyai.resident_native_brain import ResidentNativeBrainProvider, ResidentNativeStreamResult

    monkeypatch.setattr("babyai.ginger_lesson_evidence.review_identity", review_identity)
    observed = []
    candidate = "Acme BLOOM NIGHT edp 50ml tester"

    def generate(self, prompt, callback, *, max_tokens=None):
        data = json.loads(prompt.split("\n")[-1])
        observed.append((data, callback, max_tokens))
        if "attribute" in data:
            reply = dict(status="match")
        elif "title" in data:
            reply = dict(brand="Acme", fragrance="BLOOM NIGHT" if data["title"] == candidate else "Bloom")
        else:
            assert data == dict(source=dict(brand="Acme", fragrance="Bloom"),
                                candidate=dict(brand="Acme", fragrance="BLOOM NIGHT"))
            reply = dict(identity="conflict", reason="Дополнительное слово обозначает другой аромат.")
        return ResidentNativeStreamResult(text=json.dumps(reply), generated_tokens=20,
                                          output_bytes=100, stop_reason="eog")

    monkeypatch.setattr(ResidentNativeBrainProvider, "generate_stream", generate)
    provider = ResidentNativeBrainProvider(model_path=tmp_path / "model", runtime_path=tmp_path / "runtime")
    result = review(provider, candidate=candidate)
    assert result["attributes"] == values(identity="conflict")
    assert result["identity_evidence"]["candidates"][0]["candidate_fields"]["fragrance"] == "BLOOM NIGHT"
    assert [budget for _, _, budget in observed] == [96] * 5 + [256] * 3
    assert all(callback is None for _, callback, _ in observed)
    assert provider.max_tokens == 128 and result["can_match"] is False
    assert list(tmp_path.iterdir()) == []
