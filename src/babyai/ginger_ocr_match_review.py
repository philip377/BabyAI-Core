"""Conservative read-only review of Ginger CPU OCR reports.

This deliberately does NOT produce a target SKU, a Ginger UI command, or an
authorization to link anything. OCR can confuse O/R/B, Latin/Cyrillic text,
and ml/mi. We treat those as uncertainty, not confirmed equivalence.
"""
from __future__ import annotations

import json
import re
from dataclasses import asdict, dataclass
from difflib import SequenceMatcher
from pathlib import Path
from typing import Any, Mapping

from .ginger_supplier_matcher import normalize_text


_PRICE_SUFFIX = re.compile(r"\s+\d{1,6}[.,]\d{2}\s*$")
_VOLUME = re.compile(r"(?<!\d)(\d{1,4})\s*(ml|mi|m!|m1|мл|т!)(?![a-zа-я0-9])", re.I)
_TESTER = re.compile(r"(?<!\w)(tester|testeur|тестер|тест)(?!\w)", re.I)
_CONCENTRATION = re.compile(r"(?<!\w)(edp|edt|edc|extrait|parfum)(?!\w)", re.I)
_VARIANTS = frozenset({"intense", "qahwa", "elixir", "extreme", "absolu", "le", "parfum"})
_AMBIGUOUS_OCR = re.compile(r"[!?]|(?<=[a-zA-Z])[а-яА-Я]|(?<=[а-яА-Я])[a-zA-Z]")
_HEADER = ("товар цена", "название в прайсе")


@dataclass(frozen=True, slots=True)
class OcrCandidateAssessment:
    title_as_read: str
    status: str
    reason: str
    similarity: float
    reward_eligible: bool


def _extract_title(line: str) -> str:
    return _PRICE_SUFFIX.sub("", line).strip()


def _volume(text: str) -> tuple[int | None, bool]:
    items = list(_VOLUME.finditer(text))
    if len(items) != 1:
        return None, True
    raw = items[0].group(2).casefold()
    return int(items[0].group(1)), raw not in {"ml", "мл"}


def _is_unreadable_catalogue_title(title: str) -> bool:
    """Truncation and heavily fragmented OCR cannot establish tester status."""
    if "..." in title or "…" in title:
        return True
    words = title.split()
    if len(words) < 2:
        return True
    short = sum(len(token) <= 3 for token in words)
    has_details = _CONCENTRATION.search(title) or _TESTER.search(title) or _VOLUME.search(title)
    return len(words) >= 8 and short >= 5 and not has_details


def _name_for_comparison(text: str) -> str:
    # Compare a *hypothesis*, not a corrected or approved product title.
    normalized = _VOLUME.sub(lambda match: f"{match.group(1)}ml", text)
    normalized = _PRICE_SUFFIX.sub("", normalized)
    return normalize_text(normalized)


def review_ginger_cpu_ocr_report(report: Mapping[str, Any]) -> dict[str, Any]:
    """Rank only visible candidates; no catalog IDs are available in OCR."""
    if not isinstance(report, Mapping) or report.get("source") != "Ginger4 CPU OCR pilot":
        raise ValueError("Expected a Ginger4 CPU OCR report")
    if report.get("read_only") is not True or report.get("ginger_modified") is not False:
        raise ValueError("OCR report must be explicitly read-only")
    if report.get("local_only") is not True:
        raise ValueError("Refuse a report without local-only capture confirmation")

    title = report.get("selected_row_visual_candidate")
    candidates = report.get("right_catalogue_candidates")
    if not isinstance(candidates, list) or len(candidates) > 200 or any(
        not isinstance(item, str) or len(item) > 500 for item in candidates
    ):
        raise ValueError("Missing or malformed visible catalogue candidates")
    if not isinstance(title, str) or not (1 <= len(title.strip()) <= 400) or (
        report.get("selected_row_color_band_detected") is not True
    ):
        return {
            "status": "cannot_read_selected_row",
            "selected_supplier_as_read": None,
            "selected_supplier_row_verified": False,
            "safe_for_automatic_matching": False,
            "recommended_action": "manual_review_only",
            "candidates": [],
            "warnings": ["Selected supplier title or blue row is not readable"],
        }

    title = title.strip()
    source_normalized = _name_for_comparison(title)
    source_volume, source_uncertain = _volume(title)
    src_conc = _CONCENTRATION.search(title)
    source_conc = src_conc.group(1).casefold() if src_conc else None
    source_tester = bool(_TESTER.search(title))
    source_tokens = set(source_normalized.split())
    source_brand = source_normalized.split()[0] if source_normalized else ""
    source_variants = source_tokens & _VARIANTS
    results: list[OcrCandidateAssessment] = []

    for raw_line in candidates:
        clean = _extract_title(raw_line)
        if not clean or clean.casefold() in _HEADER or normalize_text(clean) in {"tovar tsena", "nazvanie v prayse"}:
            continue
        candidate_norm = _name_for_comparison(clean)
        if not candidate_norm:
            continue
        norm_tokens = set(candidate_norm.split())
        vol, vol_uncertain = _volume(clean)
        conc_match = _CONCENTRATION.search(clean)
        conc = conc_match.group(1).casefold() if conc_match else None
        similarity = round(SequenceMatcher(None, source_normalized, candidate_norm).ratio(), 4)

        if _is_unreadable_catalogue_title(clean):
            status, reason = (
                "unreadable_ocr", "Truncated or garbled name: tester, volume and variant are unknown"
            )
        elif candidate_norm.split()[0] != source_brand:
            status, reason = "different_brand", "First brand token differs"
        elif source_tester != bool(_TESTER.search(clean)):
            status, reason = "tester_mismatch", "Tester and ordinary bottle are distinct catalogue items"
        elif source_variants != (norm_tokens & _VARIANTS):
            status, reason = "variant_mismatch", "Fragrance variant differs or is unreadable"
        elif source_conc and conc and source_conc != conc:
            status, reason = "concentration_mismatch", "EDP/EDT/other concentration differs"
        elif source_volume is not None and vol is not None and source_volume != vol:
            status, reason = "volume_mismatch", "Bottle size differs"
        elif similarity < 0.78:
            status, reason = "name_mismatch", "Text is too different to trust"
        elif (similarity == 1 and not source_uncertain and not vol_uncertain
              and not _AMBIGUOUS_OCR.search(title) and not _AMBIGUOUS_OCR.search(clean)):
            status, reason = "exact_visible_text_review", "Visible text matches; catalogue SKU identity still unverified"
        else:
            status, reason = "possible_ocr_match_review", "OCR is approximate; verify name, variant and size in Ginger"

        results.append(OcrCandidateAssessment(
            title_as_read=clean, status=status, reason=reason, similarity=similarity,
            reward_eligible=(status != "unreadable_ocr")
        ))

    warnings = ["Only visible OCR text is compared, not actual Ginger catalogue IDs"]
    if source_uncertain:
        warnings.append("Supplier volume uses an uncertain OCR suffix (mi/m!/etc), not verified ml")
    if report.get("selected_supplier_row_verified") is not True:
        warnings.append("Selected supplier row identity is not verified by Ginger")
    if not results:
        warnings.append("No readable right catalogue candidates")

    return {
        "status": "review_candidates" if results else "no_readable_candidates",
        "selected_supplier_as_read": title,
        "selected_supplier_row_verified": False,
        "safe_for_automatic_matching": False,
        "recommended_action": "manual_review_only",
        "candidates": [asdict(item) for item in results],
        "warnings": warnings,
    }


def review_ginger_cpu_ocr_json(path: str | Path) -> dict[str, Any]:
    source = Path(path)
    if source.stat().st_size > 4 * 1024 * 1024:
        raise ValueError("OCR report exceeds 4 MiB")
    document = json.loads(source.read_text(encoding="utf-8-sig"))
    return review_ginger_cpu_ocr_report(document)
