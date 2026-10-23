"""Read-only plan for Ginger's per-word star search in supplier price lists.

In Ginger the operator selects a supplier row, stars individual words from the
lower-left list, then chooses a canonical product from the lower-right results.
This planner only says which *visible word indices* to star. It does not click,
search, approve, or write any supplier-to-catalogue link.
"""
from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
import re
from typing import Sequence

from .ginger_supplier_matcher import CatalogPosition, SupplierPosition, normalize_text


# Generic packaging words are checked by the matcher as product attributes,
# not used as the primary evidence that two fragrances are the same.
_SEARCH_NOISE = frozenset({
    "ml", "edp", "edt", "edc", "пв", "тв", "tester", "testeur", "тестер",
    "тест", "parfum", "perfume", "eau", "de", "вода", "парфюмированная",
    "парфюмерная", "туалетная", "духи", "spray", "спрей", "men", "women",
    "woman", "муж", "жен", "оригинал", "original", "unisex", "унисекс",
    "для", "the", "of", "от", "и",
})


@dataclass(frozen=True, slots=True)
class GingerWord:
    """One visible row in the bottom-left «Слово / Поиск» table."""
    index: int
    word: str
    normalized: str
    suggested_star: bool
    reason: str


@dataclass(frozen=True, slots=True)
class GingerWordSearchPlan:
    """Index-based star sets; indices refer to the current visible word rows."""
    words: tuple[GingerWord, ...]
    primary_indices: tuple[int, ...]
    fallback_indices: tuple[tuple[int, ...], ...]
    note: str

    def starred_words(self, indices: Sequence[int] | None = None) -> tuple[str, ...]:
        chosen = set(self.primary_indices if indices is None else indices)
        return tuple(item.word for item in self.words if item.index in chosen)


def split_supplier_words(title: str) -> list[str]:
    """Best-effort tokenization; actual Ginger UI word rows take precedence."""
    return re.findall(r"[A-Za-zА-Яа-яЁё]+|\d+(?:[.,]\d+)?", title)


def plan_ginger_word_search(
    position: SupplierPosition,
    catalogue: Sequence[CatalogPosition],
    *,
    visible_words: Sequence[str] | None = None,
    visible_search_columns: Sequence[str] | None = None,
    max_stars: int = 4,
) -> GingerWordSearchPlan:
    """Prioritize distinctive search words without assuming star click behavior.

    The UI adapter should read visible_words and visible_search_columns from
    Ginger's «Слово / Поиск» table. When unavailable, derived tokens are
    only a preview.
    It must never click a word using inferred indices without checking the UI.
    """
    if not position.title.strip() or not position.supplier_id.strip():
        raise ValueError("Supplier and title are required")
    if max_stars < 1:
        raise ValueError("max_stars must be positive")

    raw = split_supplier_words(position.title) if visible_words is None else list(visible_words)
    if len(raw) > 128:
        raise ValueError("Too many visible Ginger search words")
    if visible_search_columns is not None and len(visible_search_columns) != len(raw):
        raise ValueError("Search-column text must align with visible Ginger word rows")

    docs: list[set[str]] = [
        set(normalize_text(item.title + " " + item.brand).split())
        for item in catalogue
    ]
    document_frequency: Counter[str] = Counter()
    for words in docs:
        document_frequency.update(words)

    possible: list[tuple[int, str, int, bool]] = []
    seen: set[str] = set()
    for index, word in enumerate(raw):
        if not isinstance(word, str):
            raise ValueError("Ginger word rows must contain text")
        norm = normalize_text(word)
        if (not norm or norm in seen or norm in _SEARCH_NOISE
                or (norm.isdigit() and len(norm) <= 4) or len(norm) < 2):
            continue
        seen.add(norm)
        alternatives = {norm}
        if visible_search_columns is not None:
            search_text = visible_search_columns[index]
            if not isinstance(search_text, str) or len(search_text) > 4096:
                raise ValueError("Ginger search-column entries must be bounded text")
            # The right-hand «Поиск» cell may contain Ginger's own synonyms
            # for this word; reuse them rather than assuming exact spelling.
            alternatives.update(normalize_text(search_text).split())
        frequencies = [
            document_frequency[token] for token in alternatives
            if token not in _SEARCH_NOISE and document_frequency[token] > 0
        ]
        frequency = min(frequencies) if frequencies else 0
        possible.append((index, norm, frequency, frequency > 0))

    # First prefer words actually present in the canonical catalogue. Among
    # those, prefer rare terms: QAHWA, INTENSE, ROUGE before broad brand words.
    # An unknown spelling is not automatically assumed absent from Ginger:
    # it can be tried manually when the primary search yields no results.
    possible.sort(key=lambda x: (not x[3], x[2] if x[3] else 10**6, -len(x[1]), x[0]))
    selected = tuple(item[0] for item in possible[:max_stars])
    selected_set = set(selected)

    # Keep the most discriminative words on the first fallback. Broadening can
    # produce incorrect matches, so fallbacks are for candidate discovery only.
    fallbacks: list[tuple[int, ...]] = []
    if len(selected) > 2:
        fallbacks.append(selected[:max(2, len(selected) - 1)])
    if len(selected) > 2:
        two = selected[:2]
        if two not in fallbacks:
            fallbacks.append(two)

    words = []
    frequencies = {item[0]: item[2] for item in possible}
    for index, word in enumerate(raw):
        norm = normalize_text(word)
        if index in selected_set:
            reason = "distinctive catalogue word" if frequencies[index] else "unverified supplier word"
        else:
            reason = "keep for product verification / broaden search if needed"
        words.append(GingerWord(index, word, norm, index in selected_set, reason))

    return GingerWordSearchPlan(
        words=tuple(words),
        primary_indices=selected,
        fallback_indices=tuple(fallbacks),
        note=(
            "Stars search the catalogue only. Before binding, verify canonical product "
            "ID, brand, flanker, size, concentration, tester status and packaging. "
            "Never link automatically from a broadened word search."
        ),
    )
