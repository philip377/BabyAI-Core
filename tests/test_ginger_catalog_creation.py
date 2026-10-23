from __future__ import annotations

from dataclasses import replace

import pytest

from babyai.ginger_catalog_creation import (
    CreationBlocked,
    VariantEvidence,
    draft_missing_variant,
)
from babyai.ginger_supplier_matcher import CatalogPosition, SupplierPosition


SUPPLIER = SupplierPosition(
    supplier_id="Олег 23",
    position_id="supplier-row-193",
    title="Bruno Banani Man Intense edP 30ml",
    brand="Bruno Banani",
)
BASE = CatalogPosition(
    sku="ginger-bruno-30ml",
    title="Bruno Banani BRUNO BANANI (муж) edp 30ml",
    brand="BRUNO BANANI",
)
EVIDENCE = VariantEvidence(
    exact_catalogue_search_checked=True,
    external_product_checked=True,
    selected_supplier_row_checked=True,
    external_reference="Owner-reviewed product listing/screenshot for Man Intense edp 30ml",
)


def create(**overrides):
    args = {
        "supplier": SUPPLIER,
        "base": BASE,
        "group": "BRUNO BANANI",
        "variant": "INTENSE",
        "catalogue": [BASE, CatalogPosition(
            "ginger-loyal", "Bruno Banani LOYAL MAN (муж) edp 30ml",
            brand="BRUNO BANANI",
        )],
        "evidence": EVIDENCE,
    }
    args.update(overrides)
    return draft_missing_variant(**args)


def test_owner_bruno_banani_walkthrough_preserves_type_and_adds_variant():
    draft = create()
    assert draft.supplier_id == "Олег 23"
    assert draft.supplier_title == "Bruno Banani Man Intense edP 30ml"
    assert draft.template_sku == BASE.sku
    assert draft.group == "BRUNO BANANI"
    assert draft.new_title == "Bruno Banani BRUNO BANANI INTENSE (муж) edp 30ml"
    assert (draft.barcode, draft.price_1, draft.price_2, draft.description) == (
        "", 0.0, 0.0, "",
    )
    assert draft.requires_explicit_approval
    steps = " ".join(draft.ui_steps())
    assert "Добавить с текущими значениями" in steps
    assert "double-click" in steps
    assert "re-read" in steps.lower()


@pytest.mark.parametrize("evidence", [
    replace(EVIDENCE, exact_catalogue_search_checked=False),
    replace(EVIDENCE, external_product_checked=False),
    replace(EVIDENCE, selected_supplier_row_checked=False),
    replace(EVIDENCE, external_reference=""),
])
def test_nothing_is_prepared_when_business_evidence_is_missing(evidence):
    with pytest.raises(CreationBlocked, match="Check the exact"):
        create(evidence=evidence)


def test_relaxed_search_candidate_is_never_converted_directly_into_link():
    draft = create()
    assert draft.template_sku != ""
    assert draft.requires_explicit_approval
    assert "LOYAL MAN" not in draft.new_title
    assert draft.supplier_title != draft.template_title


@pytest.mark.parametrize("variant", ["EDP", "30", "ML", "TESTER", ""])
def test_variant_only_contains_distinctive_product_name(variant):
    with pytest.raises(CreationBlocked):
        create(variant=variant)


def test_no_variant_found_in_supplier_row_is_rejected():
    with pytest.raises(CreationBlocked, match="not present"):
        create(variant="QAHWA")


def test_creating_from_same_variant_is_rejected():
    with pytest.raises(CreationBlocked, match="already contains"):
        create(base=replace(BASE, title="Bruno Banani INTENSE (муж) edp 30ml"))


def test_missing_base_brand_or_wrong_group_is_rejected():
    with pytest.raises(CreationBlocked, match="Group and verified"):
        create(base=replace(BASE, brand=""))
    with pytest.raises(CreationBlocked, match="group differs"):
        create(group="LATTAFA")


@pytest.mark.parametrize("base", [
    replace(BASE, title="Bruno Banani BRUNO BANANI (муж) edt 30ml"),
    replace(BASE, title="Bruno Banani BRUNO BANANI (муж) edp 50ml"),
    replace(BASE, title="Bruno Banani BRUNO BANANI (муж) edp TESTER 30ml"),
    replace(BASE, title="Bruno Banani BRUNO BANANI EDP 30ml"),
])
def test_never_copy_wrong_format_concentration_size_or_tester(base):
    with pytest.raises(CreationBlocked):
        create(base=base)


def test_duplicate_title_cannot_be_created():
    exact = CatalogPosition(
        "existing",
        "Bruno Banani BRUNO BANANI INTENSE (муж) edp 30ml",
        brand="BRUNO BANANI",
    )
    with pytest.raises(CreationBlocked, match="identically named"):
        create(catalogue=[BASE, exact])


def test_alt_spelling_of_existing_variant_is_blocked():
    similar = CatalogPosition(
        "existing",
        "Bruno Banani INTENSE (муж) edp 30ml",
        brand="BRUNO BANANI",
    )
    with pytest.raises(CreationBlocked, match="Another candidate"):
        create(catalogue=[BASE, similar])


def test_no_implicit_approval_or_ginger_mutation_in_creation_plan():
    draft = create()
    assert "press OK" in " ".join(draft.ui_steps())
    assert not hasattr(draft, "execute")
    assert not hasattr(draft, "save_to_ginger")
