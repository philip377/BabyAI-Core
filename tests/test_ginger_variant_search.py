from __future__ import annotations

import pytest

from babyai.ginger_variant_search import plan_variant_search_passes
from babyai.ginger_supplier_matcher import SupplierPosition


def test_bruno_banani_owner_walkthrough_star_all_then_remove_intense():
    source = SupplierPosition("Олег 23", "Bruno Banani Man Intense edP 30ml", "row-193")
    words = ["Bruno", "Banani", "Man", "Intense", "edP", "30", "ml"]
    plan = plan_variant_search_passes(
        source, visible_words=words, variant="INTENSE"
    )
    assert plan.exact_search_word_indices == (0, 1, 2, 3, 4, 5, 6)
    assert plan.template_search_word_indices == (0, 1, 2, 4, 5, 6)
    assert plan.classify_observation(
        exact_count=0, template_count=3
    ) == "review_missing_variant_and_template"
    assert "Do not link" in plan.note


def test_exact_match_requires_review_not_new_creation():
    plan = plan_variant_search_passes(
        SupplierPosition("supplier", "BRUNO BANANI INTENSE EDP 30 ML"),
        visible_words=["BRUNO", "BANANI", "INTENSE", "EDP", "30", "ML"],
        variant="INTENSE",
    )
    assert plan.classify_observation(exact_count=1, template_count=3) == (
        "review_exact_candidates"
    )
    assert plan.classify_observation(exact_count=0, template_count=0) == (
        "manual_research_needed"
    )


def test_two_word_flanker_is_removed_atomically():
    source = SupplierPosition("vendor", "Black Opium Le Parfum 90ml")
    plan = plan_variant_search_passes(
        source, visible_words=["Black", "Opium", "Le", "Parfum", "90", "ml"],
        variant="Le Parfum",
    )
    assert plan.template_search_word_indices == (0, 1, 4, 5)


@pytest.mark.parametrize("rows,variant", [
    (["A", "B"], "INTENSE"),
    (["Intense", "INTENSE", "Man"], "INTENSE"),
    (["INTENSE"], "INTENSE"),
    ([], "INTENSE"),
])
def test_ambiguous_missing_or_no_base_search_fails_closed(rows, variant):
    with pytest.raises(ValueError):
        plan_variant_search_passes(
            SupplierPosition("supplier", "Man INTENSE"),
            visible_words=rows, variant=variant
        )


def test_invalid_result_counts_are_not_interpreted_as_no_match():
    plan = plan_variant_search_passes(
        SupplierPosition("supplier", "MAN INTENSE"),
        visible_words=["MAN", "INTENSE"], variant="INTENSE",
    )
    with pytest.raises(ValueError, match="nonnegative"):
        plan.classify_observation(exact_count=-1, template_count=1)
