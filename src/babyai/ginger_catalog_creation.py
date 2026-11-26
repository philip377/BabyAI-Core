"""Human-reviewed plan for a missing supplier perfume variant in Ginger.

Ginger is modified ONLY by a separate future UI driver, never by this module.
A broad search result is a template candidate, NOT a valid supplier link.
"""
from __future__ import annotations

from dataclasses import dataclass
import re
from typing import Sequence

from .ginger_supplier_matcher import (
    CatalogPosition,
    SupplierPosition,
    _features,
    normalize_text,
)


class CreationBlocked(ValueError):
    """Creation is unsafe without distinct evidence and manual UI review."""


@dataclass(frozen=True, slots=True)
class VariantEvidence:
    """Facts reviewed by the operator, never inferred from an empty result pane."""

    exact_catalogue_search_checked: bool
    external_product_checked: bool
    external_reference: str
    selected_supplier_row_checked: bool


@dataclass(frozen=True, slots=True)
class GingerNewProductDraft:
    """Data preview, not authority to press OK or double-click a product."""

    supplier_id: str
    supplier_title: str
    supplier_position_id: str
    template_sku: str
    template_title: str
    group: str
    new_title: str
    barcode: str
    price_1: float
    price_2: float
    description: str
    variant: str
    evidence_reference: str
    requires_explicit_approval: bool = True

    def ui_steps(self) -> tuple[str, ...]:
        """Instruction contract for a future supervised Windows adapter."""
        return (
            "Re-read the selected supplier row and Ginger search words.",
            "Repeat the exact-variant search and ensure a duplicate is absent.",
            "Choose the verified similar catalogue template, not a supplier link.",
            "Use Ginger context menu: Добавить с текущими значениями.",
            "Select the matching existing group via Ginger group picker.",
            "Set Наименование товара to the approved proposed title.",
            "Leave Штрих-код and Описание blank, set Цена 1 / Цена 2 to 0.",
            "Show exact preview, request separate approval, then press OK.",
            "Re-read the catalogue: verify new title and an actual Ginger product ID.",
            "Select the original supplier row, find the new item with full word search.",
            "Show supplier/item ID pair, request linking approval, then double-click.",
            "Re-read the supplier table and verify Товар references that item.",
        )


_TITLE_SEX_MARKER = re.compile(r"\s+\((?:муж|жен|унисекс|unisex|м|ж)\)", re.I)
_VARIANT_STOPWORDS = frozenset({
    "edp", "edt", "edc", "ml", "мл", "tester", "тестер", "spray",
    "пв", "тв", "parfum", "eau", "de", "духи", "вода",
})


def _contains_phrase(text: str, phrase: str) -> bool:
    words = normalize_text(text).split()
    needle = normalize_text(phrase).split()
    return bool(needle) and any(
        words[index : index + len(needle)] == needle
        for index in range(len(words) - len(needle) + 1)
    )


def draft_missing_variant(
    *,
    supplier: SupplierPosition,
    base: CatalogPosition,
    group: str,
    variant: str,
    catalogue: Sequence[CatalogPosition],
    evidence: VariantEvidence,
) -> GingerNewProductDraft:
    """Draft only after exact-search absence and product identity were reviewed.

    The caller must have found a *similar existing catalog item* in Ginger.
    The base item is never linked to the supplier as a substitute for the variant.
    """
    if not supplier.supplier_id.strip() or not supplier.title.strip():
        raise CreationBlocked("A selected supplier row is required")
    if not base.sku.strip() or not base.title.strip():
        raise CreationBlocked("A verified existing Ginger template item is required")
    if not (evidence.exact_catalogue_search_checked
            and evidence.external_product_checked
            and evidence.selected_supplier_row_checked
            and evidence.external_reference.strip()):
        raise CreationBlocked(
            "Check the exact catalogue search, external perfume identity, "
            "the selected supplier row and record the verification reference"
        )
    if len(evidence.external_reference) > 500:
        raise CreationBlocked("External reference is too long")

    if not group.strip() or not base.brand.strip():
        raise CreationBlocked("Group and verified base brand are required")
    if normalize_text(group) != normalize_text(base.brand):
        raise CreationBlocked("Ginger group differs from the template's brand")

    clean_variant = " ".join(variant.strip().split()).upper()
    terms = normalize_text(clean_variant).split()
    if (not terms or len(terms) > 4 or any(
        len(word) < 2 or word.isdigit() or word in _VARIANT_STOPWORDS
        for word in terms
    )):
        raise CreationBlocked("Variant must be a distinctive product name")
    if not _contains_phrase(supplier.title, clean_variant):
        raise CreationBlocked("Variant is not present in the selected supplier title")
    if _contains_phrase(base.title, clean_variant):
        raise CreationBlocked("Template already contains the proposed variant")

    source_features = _features(supplier)
    template_features = _features(base)
    if (source_features.volume_ml is None or template_features.volume_ml is None
            or source_features.concentration == ""
            or template_features.concentration == ""):
        raise CreationBlocked("Both perfume volume and concentration must be known")
    if source_features.volume_ml != template_features.volume_ml:
        raise CreationBlocked("Do not copy a template with a different bottle size")
    if source_features.concentration != template_features.concentration:
        raise CreationBlocked("Do not copy a template with another concentration")
    if source_features.is_set != template_features.is_set:
        raise CreationBlocked("Do not copy a gift set template onto a regular bottle")
    if (source_features.is_tester is not None or template_features.is_tester is not None):
        if source_features.is_tester != template_features.is_tester:
            raise CreationBlocked("Tester/retail type differs or is not established")

    # Preserve the existing Ginger name format and insert before the gender/type
    # suffix, e.g. ... BRUNO BANANI (муж) edp 30ml → ... INTENSE (муж) edp 30ml.
    marker = _TITLE_SEX_MARKER.search(base.title)
    if marker is None:
        raise CreationBlocked("Template lacks a recognized Ginger title suffix")
    prefix = base.title[:marker.start()].rstrip()
    suffix = base.title[marker.start():]
    proposed = prefix + " " + clean_variant + suffix
    normalized_proposed = normalize_text(proposed)

    for item in catalogue:
        if item.sku == base.sku:
            continue
        if normalize_text(item.title) == normalized_proposed:
            raise CreationBlocked("An identically named catalogue item already exists")
        feat = _features(item, brand_hint=base.brand)
        same_brand = (feat.brand == template_features.brand) if feat.brand else (
            normalize_text(base.brand) in normalize_text(item.title)
        )
        if (same_brand and _contains_phrase(item.title, clean_variant)
                and feat.volume_ml == template_features.volume_ml
                and feat.concentration == template_features.concentration):
            raise CreationBlocked("Another candidate with this brand, variant, volume "
                                  "and concentration may already exist")

    return GingerNewProductDraft(
        supplier_id=supplier.supplier_id,
        supplier_title=supplier.title,
        supplier_position_id=supplier.position_id,
        template_sku=base.sku,
        template_title=base.title,
        group=group.strip(),
        new_title=proposed,
        barcode="",
        price_1=0.0,
        price_2=0.0,
        description="",
        variant=clean_variant,
        evidence_reference=evidence.external_reference.strip(),
    )
