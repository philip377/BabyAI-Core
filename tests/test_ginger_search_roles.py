"""Semantic veto contracts use invented products, not expected real-model answers."""
import json

import pytest

from babyai.ginger_search_teacher import consult_search_word


REQUEST = dict(request_id="abcd" * 8, supplier="Synthetic", source="Lunora Quiet River Red Case 75 ml EDP",
               lesson="", candidate_count=90, history=[],
               words=[dict(word=word, search=word, checked=word == "Lunora")
                      for word in ["Lunora", "Quiet", "River", "Red", "Case", "75", "ml", "EDP"]])


def proposal(word="Quiet", role="name", **changes):
    return dict(request_id=REQUEST["request_id"], word=word, role=role,
                source_quote=REQUEST["source"], reason="Слово названия из исходной строки.") | changes


class Script:
    def __init__(self, *replies):
        self.replies = iter(replies)
        self.calls = []

    def generate(self, prompt):
        self.calls.append(json.loads(prompt.split("\n", 1)[1]))
        value = next(self.replies)  # An unplanned retry is a failure.
        return value if isinstance(value, str) else json.dumps(value, ensure_ascii=False)


def rule(term, role, quote):
    return dict(term=term, role=role, quote=quote)


def run_with_rules(rules, chosen=None, **request_changes):
    lesson = "\n".join(r["quote"] for r in rules)
    model = Script(dict(rules=rules), chosen or proposal())
    result = consult_search_word(model, REQUEST | dict(lesson=lesson) | request_changes)
    return result, model


def test_different_words_with_different_roles_are_compatible_and_all_evidence_survives():
    rules = [rule("Lunora", "brand", "Lunora — бренд."),
             rule("Quiet River", "name", "Quiet River — полное название."),
             rule("Red Case", "packaging", "Red Case — упаковка, не название.")]
    result, model = run_with_rules(rules)
    assert result["word"] == "Quiet" and result["can_match"] is False
    assert result["search_evidence"] == dict(version=1, definitions=rules, proposal=proposal(), blocked_reason=None)
    assert len(model.calls) == 2
    assert model.calls[0] == dict(lesson="\n".join(r["quote"] for r in rules))
    assert "source" not in model.calls[0] and "words" not in model.calls[0]
    assert model.calls[1]["definitions"] == rules


@pytest.mark.parametrize("reverse", [False, True])
@pytest.mark.parametrize("term", ["Quiet", "Quiet River"])
def test_same_or_overlapping_term_conflict_stops_before_word_selection(term, reverse):
    rules = [rule(term, "name", term + " — название."),
             rule("Quiet", "packaging", "Quiet — только упаковка.")]
    result, model = run_with_rules(list(reversed(rules)) if reverse else rules)
    assert result["word"] is None and "несовместимые роли" in result["reason"]
    assert len(model.calls) == 1 and result["search_evidence"]["proposal"] is None


def test_nested_name_and_variant_do_not_create_false_conflict():
    result, _ = run_with_rules([rule("Quiet River", "name", "Quiet River — название версии."),
                               rule("River", "variant", "River — версия.")], proposal("River", "variant"))
    assert result["word"] == "River"


@pytest.mark.parametrize("source", [REQUEST["source"], REQUEST["source"].upper()])
def test_broad_source_quote_cannot_hide_packaging_veto_even_when_model_calls_it_name(source):
    chosen = proposal("Case", source_quote=source)
    result, model = run_with_rules([rule("Red Case", "packaging", "Red Case — только упаковка.")],
                                  chosen, source=source)
    assert result["word"] is None and "упаковка" in result["reason"]
    assert result["search_evidence"]["proposal"] == chosen and len(model.calls) == 2


def test_unrelated_term_does_not_veto_substring_of_another_word():
    result, _ = run_with_rules([rule("Qui", "packaging", "Qui — упаковка.")])
    assert result["word"] == "Quiet"


def test_unknown_definition_applies_only_when_complete_term_is_in_source():
    result, model = run_with_rules([rule("Quiet", "unknown", "Значение Quiet неизвестно.")])
    assert result["word"] is None and len(model.calls) == 1
    result, model = run_with_rules([rule("Other", "unknown", "Значение Other неизвестно.")])
    assert result["word"] == "Quiet" and len(model.calls) == 2


@pytest.mark.parametrize("role", ["packaging", "service", "concentration", "volume", "tester", "gender", "unknown"])
def test_nonidentity_role_is_vetoed_without_fallback_or_retry(role):
    chosen = proposal(role=role)
    model = Script(chosen)
    result = consult_search_word(model, REQUEST)
    assert result["word"] is None and len(model.calls) == 1
    assert result["search_evidence"]["definitions"] == []
    assert result["search_evidence"]["proposal"] == chosen


@pytest.mark.parametrize("word", ["EDP", "edt", "EdC", "75", "7,5", "30ml", "100мл", "ml", "мл", "oz"])
def test_generic_attribute_cannot_pass_as_name_even_with_literal_citation(word):
    request = REQUEST | dict(source="Lunora Zephra " + word,
                             words=[dict(word=word, search=word, checked=False),
                                    dict(word="Zephra", search="", checked=False)])
    model = Script(proposal(word, source_quote=word))
    assert consult_search_word(model, request)["word"] is None
    assert len(model.calls) == 1
    assert model.calls[0]["eligible_words"] == ["Zephra"]


def test_empty_lesson_does_not_extract_and_model_refusal_is_not_reported_as_verified_conflict():
    chosen = proposal(None, "unknown", source_quote="", reason="Уроки конфликтуют: EDP — упаковка.")
    model = Script(chosen)
    result = consult_search_word(model, REQUEST)
    assert result["word"] is None and "конфликт" not in result["reason"]
    assert "Qwen не предложила" in result["reason"]
    assert result["search_evidence"]["proposal"]["reason"] == chosen["reason"]
    assert len(model.calls) == 1 and model.calls[0]["definitions"] == []


@pytest.mark.parametrize("raw_rules", [None, {}, [dict(term="Quiet", role="name", quote="made up")],
    [dict(term="Quiet", role="name", quote="River — название.")],
    [dict(term="Quiet", role="name", quote="Quiet")],
    [dict(term="Quiet", role="NAME", quote="Quiet — название.")],
    [dict(term="Quiet", role="name", quote="Quiet — название.", action="click")],
    [dict(term="Quiet", role="name", quote="Quiet — название.")] * 25])
def test_bad_lesson_evidence_never_reaches_choice(raw_rules):
    model = Script(dict(rules=raw_rules))
    # The clipped 'Quiet' quote inside Quietly must not create a whole-word term.
    with pytest.raises(ValueError):
        consult_search_word(model, REQUEST | dict(lesson="Quietly. River — название."))
    assert len(model.calls) == 1


@pytest.mark.parametrize("change", [dict(source_quote="made up"), dict(source_quote="River"),
    dict(source_quote=""), dict(source_quote=REQUEST["source"].upper()), dict(role="NAME"),
    dict(role=True), dict(word=None), dict(word=None, role="unknown"), dict(source_quote="x" * 2001)])
def test_invalid_choice_proof_fails_closed(change):
    with pytest.raises(ValueError):
        consult_search_word(Script(proposal(**change)), REQUEST)


def test_source_quote_must_not_clip_word_inside_longer_source_token():
    with pytest.raises(ValueError):
        consult_search_word(Script(proposal(source_quote="Quiet")), REQUEST | dict(source="Quietly River"))


def test_augmented_request_is_bounded_without_truncating_lessons_or_retrying():
    text = "Quiet " + "z" * 990
    rules = [rule("Quiet", "name", text)] * 12
    model = Script(dict(rules=rules))
    words = [dict(row, search="s" * 2000) if i < 3 else dict(row) for i, row in enumerate(REQUEST["words"])]
    with pytest.raises(ValueError, match="16000"):
        consult_search_word(model, REQUEST | dict(lesson=text, words=words))
    assert len(model.calls) == 1


def test_eligible_list_preserves_full_observations_but_removes_blocked_and_used_words():
    rules = [rule("Red Case", "packaging", "Red Case — упаковка.")]
    request = dict(history=[dict(word="Quiet", before_count=180, after_count=90)])
    result, model = run_with_rules(rules, proposal("River"), **request)
    assert result["word"] == "River"
    assert model.calls[1]["eligible_words"] == ["River"]
    assert model.calls[1]["words"] == REQUEST["words"]
    assert model.calls[1]["history"] == request["history"]


def test_no_eligible_word_skips_inference_and_never_fabricates_a_proposal():
    model = Script()
    request = REQUEST | dict(words=[dict(word="EDP", search="EDP", checked=False)])
    result = consult_search_word(model, request)
    assert result["word"] is None and "Нет допустимого" in result["reason"]
    assert result["search_evidence"]["proposal"] is None and not model.calls
