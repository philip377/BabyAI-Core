"""Read-only two-pass search plan for missing perfume variants in Ginger.

The full search and the relaxed search have DIFFERENT meanings:
- no full-search candidates is not proof that the product doesn't exist;
- a relaxed search excluding INTENSE may find a base template, NOT a match.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Sequence

from .ginger_supplier_matcher import SupplierPosition, normalize_text


@dataclass(frozen=True, slots=True)
class VariantSearchPasses:
    supplier_title: str
    variant: str
    exact_search_word_indices: tuple[int, ...]
    template_search_word_indices: tuple[int, ...]
    note: str

    def classify_observation(
        self, *, exact_count: int, template_count: int
    ) -> str:
        """Classifies UI result counts only; never authorizes catalogue creation."""
        if exact_count < 0 or template_count < 0:
            raise ValueError("UI result counts must be nonnegative")
        if exact_count:
            return "review_exact_candidates"
        if template_count:
            return "review_missing_variant_and_template"
        return "manual_research_needed"


def plan_variant_search_passes(
    position: SupplierPosition,
    *,
    visible_words: Sequence[str],
    variant: str,
) -> VariantSearchPasses:
    """Use the actual Ginger bottom-left word rows, not inferred pixel positions.

    Exact search is the user's observed all-star pass. A subsequent template
    discovery pass disables ONLY the distinctive variant token(s), such as
    INTENSE, while keeping brand, volume, concentration and other name words.
    """
    if not position.supplier_id.strip() or not position.title.strip():
        raise ValueError("Supplier and selected row are required")
    if not visible_words or len(visible_words) > 128:
        raise ValueError("Visible Ginger word rows are required (at most 128)")
    if any(not isinstance(word, str) or not word.strip() for word in visible_words):
        raise ValueError("Ginger word rows must be nonempty text")

    variant_tokens = normalize_text(variant).split()
    if not variant_tokens or any(len(token) < 2 or token.isdigit() for token in variant_tokens):
        raise ValueError("Distinctive variant words are required")

    normalized_rows = [normalize_text(word) for word in visible_words]
    # Each Ginger word cell normally describes one word; allow normalized
    # multiword rows only if they exactly represent the variant cell.
    matches = []
    for start in range(len(normalized_rows) - len(variant_tokens) + 1):
        if normalized_rows[start:start + len(variant_tokens)] == variant_tokens:
            matches.append(tuple(range(start, start + len(variant_tokens))))
    if len(matches) != 1:
        raise ValueError("Variant must occur exactly once in visible Ginger word rows")
    variant_indices = set(matches[0])
    remaining = tuple(i for i in range(len(visible_words)) if i not in variant_indices)
    if not remaining:
        raise ValueError("No non-variant words remain for template discovery")

    return VariantSearchPasses(
        supplier_title=position.title,
        variant=" ".join(variant_tokens).upper(),
        exact_search_word_indices=tuple(range(len(visible_words))),
        template_search_word_indices=remaining,
        note=(
            "Do not link any candidate from the relaxed search. "
            "The result is only a template for possible manually approved "
            "creation. Check existing catalogue aliases and the real perfume "
            "before making a new Ginger product."
        ),
    )
