from __future__ import annotations

import pytest

from babyai.ginger_search_plan import (
    plan_ginger_word_search,
    split_supplier_words,
)
from babyai.ginger_supplier_matcher import CatalogPosition, SupplierPosition


def _catalogue():
    return [
        CatalogPosition("100", "Bruno Banani Man Intense edP 30 ml", brand="Bruno Banani"),
        CatalogPosition("101", "Bruno Banani Man edt 30 ml", brand="Bruno Banani"),
        CatalogPosition("102", "Bruno Banani Woman 50 ml edp", brand="Bruno Banani"),
        CatalogPosition("103", "Lattafa Khamrah EDP 100 ml", brand="Lattafa"),
        CatalogPosition("104", "Lattafa Khamrah Qahwa EDP 100 ml", brand="Lattafa"),
    ]


def test_split_matches_wordwise_supplier_panel_for_compact_volume():
    assert split_supplier_words("Bruno Banani Man Intense edP 30ml") == [
        "Bruno", "Banani", "Man", "Intense", "edP", "30", "ml",
    ]


def test_starred_words_prioritize_distinctive_catalogue_names():
    plan = plan_ginger_word_search(
        SupplierPosition("Oleg 23", "Bruno Banani Man Intense edP 30 ml"),
        _catalogue(),
    )
    assert "Intense" in plan.starred_words()
    assert "Man" in plan.starred_words()
    assert "edP" not in plan.starred_words()
    assert "30" not in plan.starred_words()
    assert "ml" not in plan.starred_words()
    assert len(plan.primary_indices) <= 4
    assert plan.fallback_indices
    assert all(len(indices) < len(plan.primary_indices) for indices in plan.fallback_indices)
    assert "verify" in plan.note


def test_flanker_is_high_priority_not_discarded_as_generic_word():
    plan = plan_ginger_word_search(
        SupplierPosition("vendor", "Lattafa Khamrah Qahwa EDP 100 ml"),
        _catalogue(), max_stars=3,
    )
    assert "Qahwa" in plan.starred_words()
    assert "Khamrah" in plan.starred_words()


def test_ui_supplied_rows_are_authoritative_and_indices_preserved():
    panel = ["Banani", "Bruno", "Man", "Intense", "edP", "30", "ml"]
    plan = plan_ginger_word_search(
        SupplierPosition("vendor", "Bruno Banani Man Intense edP 30ml"),
        _catalogue(), visible_words=panel,
    )
    assert tuple(item.word for item in plan.words) == tuple(panel)
    assert all(plan.words[i].suggested_star for i in plan.primary_indices)
    assert {i for i in plan.primary_indices if panel[i] == "Intense"} == {3}


def test_excluded_attributes_are_checked_elsewhere_not_selected_for_search():
    plan = plan_ginger_word_search(
        SupplierPosition("vendor", "LATTAFA KHAMRAH QAHWA EDP 100ML TESTER"),
        _catalogue(),
    )
    assert "TESTER" not in plan.starred_words()
    assert "EDP" not in plan.starred_words()
    assert "100" not in plan.starred_words()
    assert "ML" not in plan.starred_words()


def test_absent_catalogue_does_not_elevate_unknown_metadata_terms():
    plan = plan_ginger_word_search(
        SupplierPosition("vendor", "LATTAFA KHAMRAH EDP 100ML"),
        [],
    )
    assert set(plan.starred_words()) == {"LATTAFA", "KHAMRAH"}
    assert all(i.index >= 0 for i in plan.words)


def test_invalid_plan_arguments_fail_closed():
    row = SupplierPosition("vendor", "AROMA EDP 50ml")
    with pytest.raises(ValueError, match="positive"):
        plan_ginger_word_search(row, [], max_stars=0)
    with pytest.raises(ValueError, match="required"):
        plan_ginger_word_search(SupplierPosition("", "AROMA"), [])
    with pytest.raises(ValueError, match="must contain text"):
        plan_ginger_word_search(row, [], visible_words=[10])
    with pytest.raises(ValueError, match="Too many"):
        plan_ginger_word_search(row, [], visible_words=["A"] * 129)



def test_ginger_search_column_synonyms_help_find_differently_spelled_name():
    # UI left: Lazur / CORAL / RAY; Ginger's Поиск expansion knows LAZURE.
    # Canonical Ginger catalogue spells the brand/name as LAZURE.
    source = SupplierPosition("supplier", "Lazur CORAL RAY 80ml EDP TESTER")
    catalogue = [CatalogPosition(
        "sku", "LAZURE CORAL RAY 80ml EDP TESTER", brand="LAZURE",
    )]
    words = ["Lazur", "CORAL", "RAY", "80", "ml", "EDP", "TESTER"]
    search = ['"LAZUR", "LAZURE"', '"CORAL"', '"RAY"', '"80"', '"ml"', '"edp"', '"tester"']
    plan = plan_ginger_word_search(
        source, catalogue, visible_words=words, visible_search_columns=search,
    )
    assert "Lazur" in plan.starred_words()
    assert "CORAL" in plan.starred_words()
    assert "RAY" in plan.starred_words()
    assert "EDP" not in plan.starred_words()


def test_misaligned_or_oversized_ginger_synonym_cells_fail_closed():
    row = SupplierPosition("vendor", "AROMA EDP 50ml")
    with pytest.raises(ValueError, match="align"):
        plan_ginger_word_search(row, [], visible_words=["AROMA"], visible_search_columns=[])
    with pytest.raises(ValueError, match="bounded"):
        plan_ginger_word_search(
            row, [], visible_words=["AROMA"], visible_search_columns=["a" * 4097],
        )
