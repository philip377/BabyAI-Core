import pytest
from babyai.ginger_product_condition import condition_evidence, explain_conditions
from babyai.ginger_supplier_matcher import SupplierPosition,CatalogPosition,explain_candidate


def test_missing_condition_is_unknown_not_standard():
    differences,unknown=explain_conditions("A tester с крышкой","A tester")
    assert differences == []
    assert unknown[0]["field"] == "condition_cap" and unknown[0]["candidate"] is None


@pytest.mark.parametrize("left,right,field",[
    ("A с крышкой","A без крышки","condition_cap"),
    ("A в коробке","A без коробки","condition_box"),
    ("A полный флакон","A не полный","condition_fill"),
])
def test_explicit_condition_difference(left,right,field):
    differences,unknown=explain_conditions(left,right)
    assert differences[0]["field"] == field and unknown == []


def test_conflicting_condition_requires_review():
    differences,unknown=explain_conditions("A с крышкой без крышки","A с крышкой")
    assert differences == [] and "противоречивые" in unknown[0]["message"]


def test_negated_full_bottle_never_read_as_full():
    assert condition_evidence("A не полный флакон")["fill"] == ["partial"]
    assert condition_evidence("A неполный флакон")["fill"] == ["partial"]


def test_same_or_unknown_conditions_do_not_establish_product_identity():
    result=explain_candidate(SupplierPosition("s","A EDP 50 ml с крышкой"),CatalogPosition("x","A EDP 50 ml с крышкой"))
    assert not result["can_match"] and not result["identity_verified"]
    assert not [x for x in result["differences"] if x["field"].startswith("condition_")]


def test_condition_does_not_interpret_arbitrary_product_words():
    assert all(not values for values in condition_evidence("CAP NEROLI FULL BLOOM 50 ml").values())


@pytest.mark.parametrize("title",["A не поврежденная упаковка", "A not damaged packaging", "A no full bottle"])
def test_negated_positive_condition_not_asserted(title):
    assert all(not values for values in condition_evidence(title).values())


def test_multiple_spaces_in_negation_do_not_create_full_evidence():
    assert condition_evidence("A не   полный флакон")["fill"] == ["partial"]
