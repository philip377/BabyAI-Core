"""Literal secondary search attributes; no inferred codes or product selection."""
import re

from .ginger_marking_evidence import _CANONICAL_CONCENTRATION, _negated_at
from .ginger_volume_evidence import _MEASURE, _read


_NUMBER = re.compile(r"\d+(?:[.,]\d+)?")
_METADATA_PREFIX = re.compile(
    r"(?<!\w)(?:sku|арт\.?|артикул|код|code|no\.?|№)\s*[:#№=]?\s*[([]?\s*$",
    re.IGNORECASE,
)
_NEGATED_ATTRIBUTE_PREFIX = re.compile(
    r"(?<!\w)(?:не|not|non|no|без|without)(?:\s+|[-–—])"
    r"(?:объ[её]м|volume|концентрация|concentration)\s*[:=]?\s*[([]?\s*$",
    re.IGNORECASE,
)


def literal_attribute(word):
    """Known metadata cannot become identity even when no safe attribute exists."""
    return bool(_CANONICAL_CONCENTRATION.fullmatch(word) or re.fullmatch(
        r"(?:ml|мл|oz|g|гр|г|\d+(?:[.,]\d+)?\s*(?:ml|мл|l|л|oz|g|гр|г)?)",
        word, re.IGNORECASE))


def volume_amount_spans(word, source):
    """Only exact numeric groups gain a boundary exception, including lesson vetoes.

    These are literal occurrences, not approval of a measure: ranges, sets and
    negatives still need the stricter attribute_spans checks before selection.
    """
    if not _NUMBER.fullmatch(word):
        return []
    return [match.span("amount") for match in _MEASURE.finditer(source)
            if match["amount"] == word]


def _blocked_prefix(source, start):
    return (_negated_at(source, start) or _METADATA_PREFIX.search(source[:start])
            or _NEGATED_ATTRIBUTE_PREFIX.search(source[:start]))


def _only_occurrence(word, source, span):
    # A second literal occurrence may be a model/stock number or another label.
    # Do not guess which meaning the single visible search row represents.
    return [match.span() for match in re.finditer(re.escape(word), source, re.IGNORECASE)] == [span]


def attribute_spans(word, source, role):
    """Return a source-preserving word span only for one unambiguous attribute."""
    if role == "volume":
        if _read(source)["millilitres"] is None:
            return []
        measure = next(_MEASURE.finditer(source))  # _read verified exactly one.
        if word == measure["amount"]:
            span = measure.span("amount")
        elif word.casefold() == measure[0].casefold():
            span = measure.span()
        else:
            return []  # No unit-only words, conversions or partial decimals.
        start = measure.start()
    elif role == "concentration":
        concentrations = list(_CANONICAL_CONCENTRATION.finditer(source))
        if len(concentrations) != 1:
            return []
        concentration = concentrations[0]
        if word.casefold() != concentration[0].casefold():
            return []  # No components of a full label or unexplained codes.
        span, start = concentration.span(), concentration.start()
    else:
        return []
    if _blocked_prefix(source, start) or not _only_occurrence(word, source, span):
        return []
    return [span]
