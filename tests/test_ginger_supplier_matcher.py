from __future__ import annotations

import json

import pytest

from babyai.ginger_supplier_matcher import (
    CatalogPosition,
    GingerSupplierMatcher,
    SupplierAliasStore,
    SupplierPosition,
    normalize_text,
)


def catalog():
    return [
        CatalogPosition("1", "KHAMRAH EDP 100 ML", brand="LATTAFA", barcode="6291108737194"),
        CatalogPosition("2", "KHAMRAH QAHWA EDP 100 ML", brand="LATTAFA"),
        CatalogPosition("3", "KHAMRAH EDT 100 ML", brand="LATTAFA"),
        CatalogPosition("4", "KHAMRAH EDP 50 ML", brand="LATTAFA"),
        CatalogPosition("5", "KHAMRAH EDP 100 ML TESTER", brand="LATTAFA", is_tester=True),
    ]


def test_supplier_word_order_and_language_normalization():
    assert normalize_text("ЛАТТАФА ХАМРА 100 МЛ") == "lattafa khamra 100 ml"
    matcher = GingerSupplierMatcher(catalog())
    source = SupplierPosition("supplier-a", "Lattafa Khamrah 100мл парфюмированная вода",
                              volume_ml=100, concentration="edp")
    result = matcher.recommend(source)
    assert result.status == "suggested"
    assert result.matched_sku is None
    assert result.candidates[0].sku == "1"


def test_flanker_is_not_confused_with_original():
    matcher = GingerSupplierMatcher(catalog())
    result = matcher.recommend(
        SupplierPosition("supplier-a", "Lattafa Khamrah Qahwa 100ml EDP"))
    assert result.candidates[0].sku == "2"
    assert result.status == "suggested"
    assert result.matched_sku is None


@pytest.mark.parametrize("title", [
    "KHAMRAH EDP 30 ML",
    "KHAMRAH EXTRAIT 100 ML",
    "KHAMRAH 2x50 ML EDP SET",
])
def test_incompatible_packaging_never_auto_matches(title):
    matcher = GingerSupplierMatcher([catalog()[0]])
    result = matcher.recommend(SupplierPosition("vendor", title))
    assert result.status == "unmatched"


def test_matching_preserves_concentration_and_bottle_size():
    result = GingerSupplierMatcher(catalog()).recommend(
        SupplierPosition("supplier", "LATTAFA KHAMRAH EDT 100ML"))
    assert result.candidates[0].sku == "3"
    result = GingerSupplierMatcher(catalog()).recommend(
        SupplierPosition("supplier", "LATTAFA KHAMRAH EDP 50ML"))
    assert result.candidates[0].sku == "4"


def test_tester_not_mapped_to_known_retail():
    matcher = GingerSupplierMatcher([
        CatalogPosition("retail", "AROMA 100 ML EDP", is_tester=False)
    ])
    result = matcher.recommend(SupplierPosition("vendor", "AROMA 100ML EDP TESTER"))
    assert result.status == "unmatched"


def test_tester_flag_missing_is_manual_review():
    result = GingerSupplierMatcher([
        CatalogPosition("unspecified", "AROMA 100 ML EDP")
    ]).recommend(SupplierPosition("vendor", "AROMA 100 ML EDP TESTER"))
    assert result.status == "review"


def test_conflicting_barcodes_cannot_match():
    matcher = GingerSupplierMatcher([
        CatalogPosition("a", "AROMA 100 ML EDP", barcode="123456789")
    ])
    result = matcher.recommend(
        SupplierPosition("vendor", "AROMA 100 ML EDP", barcode="87654321"))
    assert result.status == "unmatched"


def test_duplicate_canonical_names_require_manual_review():
    matcher = GingerSupplierMatcher([
        CatalogPosition("a", "AROMA 100 ML EDP"),
        CatalogPosition("b", "AROMA 100 ML EDP"),
    ])
    result = matcher.recommend(SupplierPosition("vendor", "AROMA 100 ML EDP"))
    assert result.status == "review"
    assert result.matched_sku is None
    assert [c.sku for c in result.candidates] == ["a", "b"]


def test_known_alias_is_supplier_scoped_and_only_active_after_confirmation(tmp_path):
    store = SupplierAliasStore(tmp_path / "ginger" / "aliases.json")
    matcher = GingerSupplierMatcher(catalog(), store)
    vendor_row = SupplierPosition("vendor-A", "LATTAFA KHAMRAH EDP 100ML", position_id="SKU-42")
    before = matcher.recommend(vendor_row)
    assert before.status == "suggested"
    assert before.matched_sku is None
    assert not store.path.exists()

    store.confirm(vendor_row, "1")
    after = matcher.recommend(vendor_row)
    assert after.status == "known"
    assert after.matched_sku == "1"

    other = matcher.recommend(
        SupplierPosition("vendor-B", vendor_row.title, position_id=vendor_row.position_id))
    assert other.status == "suggested"
    assert other.matched_sku is None
    payload = json.loads(store.path.read_text(encoding="utf-8"))
    assert payload["schema_version"] == 1


def test_reused_supplier_article_requires_review_instead_of_stale_alias(tmp_path):
    store = SupplierAliasStore(tmp_path / "aliases.json")
    matcher = GingerSupplierMatcher(catalog(), store)
    old = SupplierPosition("vendor", "LATTAFA KHAMRAH EDP 100 ML", position_id="A42")
    store.confirm(old, "1")

    reused = SupplierPosition("vendor", "LATTAFA KHAMRAH QAHWA EDP 100 ML", position_id="A42")
    result = matcher.recommend(reused)
    assert result.matched_sku is None
    assert result.candidates[0].sku == "2"


def test_alias_reassignment_requires_explicit_replace(tmp_path):
    store = SupplierAliasStore(tmp_path / "aliases.json")
    source = SupplierPosition("vendor", "Khamrah edp 100ml", "article-1")
    store.confirm(source, "1")
    with pytest.raises(ValueError, match="explicit replace"):
        store.confirm(source, "2")
    assert store.get(source)[0] == "1"
    store.confirm(source, "2", replace=True)
    assert store.get(source)[0] == "2"


def test_renamed_vendor_row_and_stale_catalogue_do_not_reuse_alias(tmp_path):
    store = SupplierAliasStore(tmp_path / "aliases.json")
    old = SupplierPosition("vendor", "AROMA A 100ml EDP", "article-1")
    store.confirm(old, "sku1")
    matcher = GingerSupplierMatcher([
        CatalogPosition("sku1", "AROMA A 100ml EDP"),
        CatalogPosition("sku2", "AROMA B 100ml EDP"),
    ], store)
    changed = SupplierPosition("vendor", "AROMA B 100ml EDP", "article-1")
    assert matcher.recommend(changed).matched_sku is None

    removed = GingerSupplierMatcher([CatalogPosition("sku2", "AROMA B 100ml EDP")], store)
    assert removed.recommend(old).matched_sku is None


def test_invalid_alias_file_fails_closed(tmp_path):
    path = tmp_path / "aliases.json"
    path.write_text('{"schema_version":1,"mappings":{"a":{"x":{"sku":1}}}}', encoding="utf-8")
    store = SupplierAliasStore(path)
    with pytest.raises(ValueError, match="Invalid supplier mapping entry"):
        store.get(SupplierPosition("a", "AROMA", "x"))


def test_bad_input_and_duplicate_catalogue_ids_are_rejected():
    with pytest.raises(ValueError, match="unique"):
        GingerSupplierMatcher([
            CatalogPosition("duplicate", "AROMA 50ML"),
            CatalogPosition("duplicate", "AROMA 100ML"),
        ])
    with pytest.raises(ValueError, match="required"):
        GingerSupplierMatcher([]).recommend(SupplierPosition("", "AROMA"))
    with pytest.raises(ValueError, match="positive"):
        GingerSupplierMatcher([]).recommend(SupplierPosition("vendor", "AROMA"), limit=0)
