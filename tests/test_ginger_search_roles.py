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
                reason="Слово названия из исходной строки.") | changes


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
    assert result["search_evidence"] == dict(version=2, definitions=rules, proposal=proposal(),
        source_evidence=dict(origin="observed_source", derived_by="host", quote="Quiet"), blocked_reason=None)
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
def test_host_source_evidence_cannot_override_packaging_veto_even_when_model_calls_it_name(source):
    chosen = proposal("Case")
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
def test_unconfirmed_role_is_vetoed_without_fallback_or_retry(role):
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
    model = Script(proposal(word))
    assert consult_search_word(model, request)["word"] is None
    assert len(model.calls) == 1
    attribute_role = ("concentration" if word.lower() in {"edp", "edt", "edc"} else
                      "volume" if word in {"30ml", "100мл"} else None)
    assert model.calls[0]["eligible_words"] == ([word, "Zephra"] if attribute_role else ["Zephra"])
    assert model.calls[0].get("attribute_roles", {}) == ({word: attribute_role} if attribute_role else {})


def test_empty_lesson_does_not_extract_and_model_refusal_is_not_reported_as_verified_conflict():
    chosen = proposal(None, "unknown", reason="Уроки конфликтуют: EDP — упаковка.")
    model = Script(chosen)
    result = consult_search_word(model, REQUEST)
    assert result["word"] is None and "конфликт" not in result["reason"]
    assert "Qwen не предложила" in result["reason"]
    assert result["search_evidence"]["proposal"]["reason"] == chosen["reason"]
    assert result["search_evidence"]["source_evidence"] is None
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
    dict(role=True), dict(word=None), dict(source_evidence={}), dict(source_quote="x" * 2001)])
def test_old_proof_fields_and_invalid_choice_fail_closed(change):
    with pytest.raises(ValueError):
        consult_search_word(Script(proposal(**change)), REQUEST)


def test_source_quote_must_not_clip_word_inside_longer_source_token():
    with pytest.raises(ValueError):
        consult_search_word(Script(proposal()), REQUEST | dict(source="Quietly River"))


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
    assert model.calls[1]["eligible_words"] == ["River", "75", "EDP"]
    assert model.calls[1]["attribute_roles"] == {"75": "volume", "EDP": "concentration"}
    assert model.calls[1]["words"] == REQUEST["words"]
    assert model.calls[1]["history"] == request["history"]


def test_no_eligible_word_skips_inference_and_never_fabricates_a_proposal():
    model = Script()
    request = REQUEST | dict(words=[dict(word="ml", search="ml", checked=False)])
    result = consult_search_word(model, request)
    assert result["word"] is None and "Нет допустимого" in result["reason"]
    assert result["search_evidence"]["proposal"] is None and not model.calls
    assert result["search_evidence"]["source_evidence"] is None


@pytest.mark.parametrize("word,source,expected", [
    ("Quiet", "Lunora Quiet River", "Quiet"),
    ("Quiet", "Lunora QUIET River", "QUIET"),
    ("Лист", "🧪 Lunora лист 50ml", "лист"),
    ("Ω83 / nuit", "Lunora (Ω83 / NUIT) 50ml", "Ω83 / NUIT"),
    ("Nüit", "Lunora NÜIT 50ml", "NÜIT"),
])
def test_host_derives_literal_source_case_and_does_not_change_model_proposal(word, source, expected):
    chosen = proposal(word, reason="Гарантированно уникален; я уже нашёл товар.")
    request = REQUEST | dict(source=source, words=[dict(word=word, search="", checked=False)])
    result = consult_search_word(Script(chosen), request)
    assert result["word"] == word and result["can_match"] is False
    evidence = result["search_evidence"]
    assert evidence["proposal"] == chosen and "source_quote" not in evidence["proposal"]
    assert evidence["source_evidence"] == dict(origin="observed_source", derived_by="host", quote=expected)
    assert "уникален" not in result["reason"] and "нашёл" not in result["reason"]
    assert "по оценке Qwen" in result["reason"] and "ещё не проверен" in result["reason"]


def test_lesson_definition_stays_separate_from_computed_source_word():
    quote = "Quiet River — название, оба слова важны."
    chosen = proposal("River", reason=quote)
    result, model = run_with_rules([rule("Quiet River", "name", quote)], chosen)
    evidence = result["search_evidence"]
    assert result["word"] == "River" and evidence["source_evidence"]["quote"] == "River"
    assert evidence["definitions"][0]["quote"] == quote
    assert evidence["proposal"]["reason"] == quote and quote not in result["reason"]
    assert len(model.calls) == 2


def test_one_clean_source_occurrence_does_not_hide_a_blocked_occurrence():
    result, _ = run_with_rules([rule("Red Case", "packaging", "Red Case — упаковка.")], proposal("Case"),
                              source="Lunora Quiet Case Red Case 50ml")
    assert result["word"] is None and "упаковка" in result["reason"]
    assert result["search_evidence"]["source_evidence"]["quote"] == "Case"


def test_role_and_word_are_checked_even_when_an_exact_source_span_exists():
    request = REQUEST | dict(history=[dict(word="Quiet", before_count=180, after_count=90)])
    with pytest.raises(ValueError, match="Повтор"):
        consult_search_word(Script(proposal()), request)
    request = REQUEST | dict(words=REQUEST["words"] + [dict(word="Quiet", search="", checked=False)])
    with pytest.raises(ValueError, match="неоднозначно"):
        consult_search_word(Script(proposal()), request)
    result = consult_search_word(Script(proposal(role="service")), REQUEST)
    assert result["word"] is None and result["search_evidence"]["source_evidence"]["quote"] == "Quiet"


@pytest.mark.parametrize("letter", ["X", "Я", "Ω", "ü"])
def test_unexplained_single_letter_in_two_contexts_stops_before_choice(letter):
    request = REQUEST | dict(source=f"Lunora {letter}*name ({letter}) 100ml edt",
        words=[dict(word=word, search="", checked=checked) for word, checked in
               [("Lunora", True), (letter, None), ("name", True), ("100", True), ("ml", None), ("edt", True)]])
    model = Script()
    result = consult_search_word(model, request)
    assert result["word"] is None and result["can_match"] is False
    assert "Одиночная буква" in result["reason"] and "во всех местах" in result["reason"]
    assert result["reason"] == result["search_evidence"]["blocked_reason"]
    assert result["search_evidence"]["proposal"] is None
    assert result["search_evidence"]["source_evidence"] is None and not model.calls


@pytest.mark.parametrize("source,terms,allowed", [
    ("Lunora X*name (X)", ["X*name"], False),
    ("Lunora X*name (X)", ["(X)"], False),
    ("Lunora X*name (X)", ["X*name", "(X)"], True),
    ("Lunora X*name X*name", ["X*name"], True),
    ("Lunora X*name", ["X*name"], True),
    ("Lunora X*name (X)", ["X"], True),
])
def test_identity_lesson_must_cover_every_single_letter_occurrence(source, terms, allowed):
    rules = [rule(term, "name", term + " — название аромата.") for term in terms]
    chosen = proposal("X")
    result, model = run_with_rules(rules, chosen, source=source,
                                  words=[dict(word="X", search="", checked=False)])
    assert result["search_evidence"]["definitions"] == rules
    if allowed:
        assert result["word"] == "X" and len(model.calls) == 2
        assert model.calls[1]["eligible_words"] == ["X"]
        assert result["search_evidence"]["proposal"] == chosen
    else:
        assert result["word"] is None and len(model.calls) == 1
        assert "во всех местах" in result["reason"]
        assert result["search_evidence"]["proposal"] is None


@pytest.mark.parametrize("role", ["brand", "name", "variant"])
@pytest.mark.parametrize("letter,source", [("X", "Lunora X 50ml"), ("я", "Lunora Я 50ml")])
def test_real_single_letter_identity_with_explicit_lesson_is_preserved(role, letter, source):
    rules = [rule(letter, role, letter + " — явное обозначение идентичности товара.")]
    result, model = run_with_rules(rules, proposal(letter, role), source=source,
                                  words=[dict(word=letter, search="", checked=None)])
    assert result["word"] == letter and len(model.calls) == 2
    assert result["search_evidence"]["source_evidence"]["quote"] in source


@pytest.mark.parametrize("role", ["brand", "name", "variant"])
def test_model_cannot_restore_an_excluded_single_letter_or_force_a_fallback(role):
    chosen = proposal("X", role, reason="Это несомненно точное название.")
    request = REQUEST | dict(source="Lunora X*name (X) Zephra",
        words=[dict(word=word, search="", checked=False) for word in ["X", "Zephra"]])
    model = Script(chosen)
    result = consult_search_word(model, request)
    assert model.calls[0]["eligible_words"] == ["Zephra"]
    assert result["word"] is None and "Одиночная буква" in result["reason"]
    assert result["search_evidence"]["proposal"] == chosen and len(model.calls) == 1


@pytest.mark.parametrize("word", ["*", "--", "()", "…", "_", "🧪"])
@pytest.mark.parametrize("role", ["brand", "name", "variant"])
def test_punctuation_only_cannot_be_identity_even_with_an_identity_lesson(word, role):
    rules = [rule(word, role, word + " — название.")]
    chosen = proposal(word, role)
    result, model = run_with_rules(rules, chosen, source="Lunora Zephra " + word,
        words=[dict(word=item, search="", checked=False) for item in [word, "Zephra"]])
    assert model.calls[1]["eligible_words"] == ["Zephra"]
    assert result["word"] is None and "только из знаков" in result["reason"]
    assert result["search_evidence"]["proposal"] == chosen and len(model.calls) == 2


def test_only_punctuation_gives_a_clear_refusal_without_choice():
    model = Script()
    result = consult_search_word(model, REQUEST | dict(source="Lunora --",
        words=[dict(word="--", search="", checked=False)]))
    assert result["word"] is None and "только из знаков" in result["reason"]
    assert result["search_evidence"]["proposal"] is None and not model.calls


@pytest.mark.parametrize("word", ["H24", "No.5", "X*name", "X Name", "Étoile du soir"])
def test_complete_alphanumeric_and_multiword_names_do_not_need_single_letter_exception(word):
    model = Script(proposal(word))
    result = consult_search_word(model, REQUEST | dict(source="Lunora " + word + " 50ml",
        words=[dict(word=word, search="", checked=False)]))
    assert result["word"] == word and model.calls[0]["eligible_words"] == [word]
    assert len(model.calls) == 1


@pytest.mark.parametrize("role", ["gender", "service", "packaging"])
def test_single_letter_identity_coverage_cannot_override_a_nonidentity_occurrence(role):
    rules = [rule("X*name", "name", "X*name — название."),
             rule("(X)", role, "(X) — дополнительная характеристика, не название.")]
    chosen = proposal("X")
    result, model = run_with_rules(rules, chosen, source="Lunora X*name (X) Zephra",
        words=[dict(word=word, search="", checked=False) for word in ["X", "Zephra"]])
    assert result["word"] is None and "Из урока извлечена роль" in result["reason"]
    assert model.calls[1]["eligible_words"] == ["Zephra"]
    assert result["search_evidence"]["proposal"] == chosen


def test_single_letter_identity_and_gender_definitions_still_conflict_before_choice():
    rules = [rule("X", "name", "X — название."), rule("X", "gender", "X — пол товара.")]
    result, model = run_with_rules(rules, source="Lunora X",
                                  words=[dict(word="X", search="", checked=False)])
    assert result["word"] is None and "несовместимые роли" in result["reason"]
    assert len(model.calls) == 1 and result["search_evidence"]["proposal"] is None
