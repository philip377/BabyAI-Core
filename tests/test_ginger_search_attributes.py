"""Source-bound attribute eligibility, using fake replies rather than model quality."""
import json

import pytest

from babyai.ginger_search_teacher import consult_search_word


class Script:
    def __init__(self, *replies):
        self.replies = iter(replies)
        self.calls = []

    def generate(self, prompt):
        self.calls.append(json.loads(prompt.split("\n", 1)[1]))
        return json.dumps(next(self.replies), ensure_ascii=False)  # No implicit retries.


def request(source, *words, lesson="", history=None):
    return dict(request_id="b" * 32, supplier="Synthetic supplier", source=source, lesson=lesson,
                words=[dict(word=word, search="", checked=False) for word in words],
                candidate_count=213, history=history or [])


def proposal(word, role):
    return dict(request_id="b" * 32, word=word, role=role,
                reason="Уникальный товар найден, можно сопоставлять.")


def definition(term, role):
    return dict(term=term, role=role, quote=f"{term} — роль {role}.")


def with_rules(payload, rules, chosen):
    model = Script(dict(rules=rules), chosen)
    result = consult_search_word(model, payload | dict(lesson="\n".join(r["quote"] for r in rules)))
    return result, model


@pytest.mark.parametrize("source,word,quote", [
    ("Oria Bloom 100ml", "100", "100"),
    ("Oria Bloom 100мл", "100", "100"),
    ("Oria Bloom 100 мл", "100", "100"),
    ("Oria Bloom 7,5мл", "7,5", "7,5"),
    ("Oria Bloom 7.50 ml", "7.50", "7.50"),
    ("Oria Bloom 0.1L", "0.1", "0.1"),
    ("Oria Bloom 1 л", "1", "1"),
    ("Oria Bloom (100ML)", "100ml", "100ML"),
    ("Oria Bloom 100 мл", "100 МЛ", "100 мл"),
    ("Oria Z9 К4 No5 Fragrance24 100ml", "100", "100"),
])
def test_one_explicit_measure_supports_only_exact_visible_amount_or_full_measure(source, word, quote):
    chosen = proposal(word, "volume")
    model = Script(chosen)
    result = consult_search_word(model, request(source, word))
    assert result["word"] == word and result["can_match"] is False
    assert model.calls[0]["eligible_words"] == [word]
    assert model.calls[0]["attribute_roles"] == {word: "volume"}
    assert result["search_evidence"] == dict(version=2, definitions=[], proposal=chosen,
        source_evidence=dict(origin="observed_source", derived_by="host", quote=quote), blocked_reason=None)
    assert len(model.calls) == 1
    assert "сопоставлять" not in result["reason"] and "найден" not in result["reason"]


def test_actual_kenzo_request_can_choose_100_without_reusing_checked_or_unexplained_L():
    payload = request("Kenzo L*eau (L) 100ml edt", "Kenzo", "L", "*", "eau", "(", ")", "100", "ml", "edt")
    for row in payload["words"]:
        if row["word"] in {"Kenzo", "L", "*", "eau", "edt"}:
            row["checked"] = True
        else:
            row["checked"] = None
    model = Script(proposal("100", "volume"))
    result = consult_search_word(model, payload)
    assert result["word"] == "100" and len(model.calls) == 1
    assert model.calls[0]["eligible_words"] == ["100"]
    assert model.calls[0]["words"] == payload["words"]
    assert model.calls[0]["attribute_roles"] == {"100": "volume"}


@pytest.mark.parametrize("notation,word", [
    ("100", "100"), ("100ml", "ml"), ("100мл", "мл"), ("1L", "L"),
    ("0.1L", "100"), ("7,5ml", "7.5"), ("7.5ml", "7"), ("7.5ml", "5"),
    ("100 ml", "100ml"), ("100ml", "10"), ("H100", "100"), ("No.100", "100"),
    ("100ml No.100", "100"), ("No.100 Bloom 100ml", "100"),
    ("H100 Bloom 100ml", "100"), ("5100 Bloom 100ml", "100"),
    ("100ml 100", "100"), ("100ml 100ml", "100ml"),
    ("2x100ml", "100"), ("100ml x 2", "100"), ("100ml (x2)", "100"),
    ("100ml+50ml", "100"), ("50/100ml", "100"), ("50-100ml", "100"),
    ("100ml 2-pack", "100"), ("set 100ml", "100"), ("набор 100ml", "100"),
    ("2 шт. 100ml", "100"), ("12 100ml", "100"), ("1 250ml", "250"),
    ("примерно 100ml", "100"), ("~100ml", "100"), ("100ml ±5", "100"),
    ("100ml approx.", "100"), ("-100ml", "100"), ("0ml", "0"),
    ("EDP100ml", "100"), ("100ml2", "100"), (".5ml", "5"),
    ("1e3ml", "3"), ("1,234.5ml", "234.5"),
    ("не 100ml", "100"), ("not 100ml", "100"), ("non-100ml", "100"),
    ("без 100ml", "100"), ("without 100ml", "100"), ("no 100ml", "100"),
    ("не объём: 100ml", "100"), ("not volume 100ml", "100"),
    ("SKU 100ml", "100"), ("SKU:100ml", "100"), ("арт. 100ml", "100"),
    ("артикул 100ml", "100"), ("код: 100ml", "100"), ("code 100ml", "100"),
    ("№ 100ml", "100"), ("No 100ml", "100"),
])
def test_ambiguous_composite_negated_stock_number_and_unit_only_skip_choice(notation, word):
    model = Script()
    result = consult_search_word(model, request("Oria " + notation, word))
    assert result["word"] is None and result["can_match"] is False
    assert result["search_evidence"]["proposal"] is None and not model.calls


@pytest.mark.parametrize("word,role", [("100", "name"), ("100", "brand"), ("100", "variant"),
    ("100", "concentration"), ("100ml", "name"), ("EDP", "name"), ("EDP", "volume"),
    ("eau de parfum", "variant"), ("extrait", "name"), ("туалетная вода", "name")])
def test_model_cannot_promote_attributes_to_identity_or_swap_their_roles(word, role):
    source = "Oria Bloom 100ml " + (word if word not in {"100", "100ml"} else "EDP")
    chosen = proposal(word, role)
    model = Script(chosen)
    result = consult_search_word(model, request(source, "Bloom", word))
    assert result["word"] is None and len(model.calls) == 1
    assert result["search_evidence"]["proposal"] == chosen
    assert result["search_evidence"]["source_evidence"]["quote"].casefold() == word.casefold()
    assert "Bloom" in model.calls[0]["eligible_words"]  # No replacement chosen by host.


@pytest.mark.parametrize("label", ["EDP", "edt", "EdC", "eau de parfum", "eau de toilette",
    "eau de cologne", "парфюмерная вода", "туалетная вода", "одеколон", "parfum", "extrait", "extrait de parfum"])
def test_one_canonical_full_concentration_is_a_secondary_search_role(label):
    model = Script(proposal(label, "concentration"))
    result = consult_search_word(model, request("Oria Bloom " + label.upper(), label))
    assert result["word"] == label and len(model.calls) == 1
    assert model.calls[0]["attribute_roles"] == {label: "concentration"}
    assert result["search_evidence"]["source_evidence"]["quote"] == label.upper()


@pytest.mark.parametrize("source,word", [
    ("EDP EDT", "EDP"), ("EDP EDP", "EDP"), ("eau de parfum EDP", "EDP"),
    ("eau de parfum", "parfum"), ("eau de toilette", "toilette"),
    ("extrait de parfum", "parfum"), ("EDPrivate EDP", "EDP"),
    ("not EDP", "EDP"), ("не EDT", "EDT"), ("non-EDC", "EDC"),
    ("without parfum", "parfum"), ("без extrait", "extrait"),
    ("не концентрация EDP", "EDP"), ("SKU EDP", "EDP"),
])
def test_unconfirmed_concentration_cannot_be_forced_by_a_model(source, word):
    chosen = proposal(word, "concentration")
    model = Script(chosen)
    result = consult_search_word(model, request("Oria Bloom " + source, "Bloom", word))
    assert result["word"] is None and result["search_evidence"]["proposal"] == chosen
    assert word not in model.calls[0].get("attribute_roles", {})
    assert len(model.calls) == 1


def test_a_lesson_role_does_not_turn_an_arbitrary_code_into_a_literal_concentration():
    chosen = proposal("K4", "concentration")
    result, model = with_rules(request("Oria Bloom K4", "Bloom", "K4"),
                               [definition("K4", "concentration")], chosen)
    assert result["word"] is None and len(model.calls) == 2
    assert model.calls[1]["eligible_words"] == ["Bloom"]
    assert result["search_evidence"]["proposal"] == chosen


@pytest.mark.parametrize("role", ["name", "brand", "variant", "packaging", "service", "tester", "gender", "concentration", "unknown"])
@pytest.mark.parametrize("term", ["100", "100ml"])
def test_numeric_lesson_blocks_the_same_amount_inside_measure_even_without_whole_word_boundary(role, term):
    result, model = with_rules(request("Oria 100ml", "100"),
                               [definition(term, role)], proposal("100", "volume"))
    assert result["word"] is None and len(model.calls) == 1
    assert result["search_evidence"]["proposal"] is None


@pytest.mark.parametrize("term,role,source,word", [
    ("100", "volume", "Oria 100ml", "100"),
    ("100ml", "volume", "Oria 100ml", "100"),
    ("EDP", "concentration", "Oria EDP", "EDP"),
])
def test_compatible_literal_attribute_definition_is_preserved(term, role, source, word):
    result, model = with_rules(request(source, word), [definition(term, role)], proposal(word, role))
    assert result["word"] == word and len(model.calls) == 2


@pytest.mark.parametrize("reverse", [False, True])
@pytest.mark.parametrize("other", ["name", "service", "packaging", "concentration"])
def test_numeric_lesson_conflict_stops_before_choice_in_both_orders(reverse, other):
    rules = [definition("100", other), definition("100ml", "volume")]
    result, model = with_rules(request("Oria Bloom 100ml", "Bloom", "100"),
                               rules[::-1] if reverse else rules, proposal("100", "volume"))
    assert result["word"] is None and len(model.calls) == 1
    assert "несовместимые роли" in result["reason"]


@pytest.mark.parametrize("reverse", [False, True])
def test_identity_and_attribute_lessons_for_different_words_do_not_conflict(reverse):
    rules = [definition("Bloom", "name"), definition("100", "volume"), definition("EDP", "concentration")]
    result, model = with_rules(request("Oria Bloom 100ml EDP", "Bloom", "100", "EDP"),
                               rules[::-1] if reverse else rules, proposal("100", "volume"))
    assert result["word"] == "100" and len(model.calls) == 2
    assert model.calls[1]["eligible_words"] == ["Bloom", "100", "EDP"]


@pytest.mark.parametrize("term", ["10", "1", "00", "100m", "loom"])
def test_numeric_exception_does_not_relax_partial_lesson_or_identity_boundaries(term):
    result, model = with_rules(request("Oria Bloom 100ml", "Bloom", "100"),
                               [definition(term, "unknown")], proposal("100", "volume"))
    assert result["word"] == "100" and len(model.calls) == 2


@pytest.mark.parametrize("state", ["checked", "history", "duplicate"])
def test_attribute_cannot_bypass_checkbox_history_or_unique_visible_word_guards(state):
    payload = request("Oria 100ml", "100")
    if state == "checked":
        payload["words"][0]["checked"] = True
    elif state == "history":
        payload["history"] = [dict(word="100", before_count=213, after_count=90)]
    else:
        payload["words"].append(dict(payload["words"][0]))
    model = Script()
    result = consult_search_word(model, payload)
    assert result["word"] is None and not model.calls


def test_null_remains_null_even_with_a_safe_literal_attribute():
    chosen = proposal(None, "unknown")
    model = Script(chosen)
    result = consult_search_word(model, request("Oria 100ml", "100"))
    assert result["word"] is None and result["search_evidence"]["source_evidence"] is None
    assert result["search_evidence"]["proposal"] == chosen and len(model.calls) == 1


def test_bad_model_schema_stays_error_and_never_selects_the_available_attribute():
    model = Script(proposal("100", "volume") | dict(can_match=True))
    with pytest.raises(ValueError):
        consult_search_word(model, request("Oria 100ml", "100"))
    assert len(model.calls) == 1
