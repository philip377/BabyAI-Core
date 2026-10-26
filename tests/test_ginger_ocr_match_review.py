from __future__ import annotations

import pytest

from babyai.ginger_ocr_match_review import review_ginger_cpu_ocr_report


def real_world_report():
    return {
        "source": "Ginger4 CPU OCR pilot",
        "read_only": True, "ginger_modified": False, "local_only": True,
        "selected_row_color_band_detected": True,
        "selected_supplier_row_verified": False,
        "selected_row_visual_candidate": "Bois 1920 Oro Rosso edp 100 mi",
        "right_catalogue_candidates": [
            "Товар Цена",
            "Bois 1920 ОВО ROSSO edp 100ml 82,00",
            "Bois 1920 ORO ROSSO edp 100ml (тестер) 0.00",
            "Paolo Сай ОВО ROSSA (myx) edp 100m! 0.00",
        ],
    }


def test_actual_ginger_ocr_report_keeps_three_different_types():
    report = review_ginger_cpu_ocr_report(real_world_report())
    assert report["status"] == "review_candidates"
    assert report["selected_supplier_as_read"] == "Bois 1920 Oro Rosso edp 100 mi"
    assert [c["status"] for c in report["candidates"]] == [
        "possible_ocr_match_review", "tester_mismatch", "different_brand",
    ]
    assert report["safe_for_automatic_matching"] is False
    assert report["selected_supplier_row_verified"] is False
    assert "uncertain OCR suffix" in " ".join(report["warnings"])


def test_even_exact_visible_text_does_not_grant_erp_write():
    report = real_world_report()
    report["selected_row_visual_candidate"] = "Bois 1920 Oro Rosso edp 100ml"
    report["right_catalogue_candidates"] = ["Bois 1920 Oro Rosso edp 100ml 82,00"]
    result = review_ginger_cpu_ocr_report(report)
    assert result["candidates"][0]["status"] == "exact_visible_text_review"
    assert result["safe_for_automatic_matching"] is False
    assert result["recommended_action"] == "manual_review_only"
    assert "sku" not in result["candidates"][0]


@pytest.mark.parametrize(("source","target","expected"), [
    ("LATTAFA KHAMRAH QAHWA EDP 100ml", "LATTAFA KHAMRAH EDP 100ml", "variant_mismatch"),
    ("BRUNO BANANI INTENSE EDP 30ml", "BRUNO BANANI EDP 30ml", "variant_mismatch"),
    ("BRUNO BANANI EDP 30ml", "BRUNO BANANI EDT 30ml", "concentration_mismatch"),
    ("BRUNO BANANI EDP 30ml", "BRUNO BANANI EDP 50ml", "volume_mismatch"),
    ("BRUNO BANANI EDP 30ml", "BRUNO BANANI EDP 30ml TESTER", "tester_mismatch"),
])
def test_hard_variant_volume_and_tester_guards(source, target, expected):
    report = real_world_report()
    report["selected_row_visual_candidate"] = source
    report["right_catalogue_candidates"] = [target]
    result = review_ginger_cpu_ocr_report(report)
    assert result["candidates"][0]["status"] == expected


def test_absent_supplier_vision_blocks_matching():
    report = real_world_report()
    report["selected_row_color_band_detected"] = False
    result = review_ginger_cpu_ocr_report(report)
    assert result["status"] == "cannot_read_selected_row"
    assert result["candidates"] == []


def test_unsafe_or_wrong_report_is_rejected():
    for broken in (
        {"read_only": False}, {"ginger_modified": True}, {"local_only": False},
        {"source": "other app"},
    ):
        report = real_world_report()
        report.update(broken)
        with pytest.raises(ValueError):
            review_ginger_cpu_ocr_report(report)


def test_malformed_candidate_list_is_rejected():
    report = real_world_report()
    report["right_catalogue_candidates"] = ["ok", None]
    with pytest.raises(ValueError, match="malformed"):
        review_ginger_cpu_ocr_report(report)



def test_live_ginger_bronze_goddess_truncated_catalogue_not_a_tester_mismatch():
    report = real_world_report()
    report["selected_row_visual_candidate"] = (
        "Estee Lauder Bronze Goddess Soleil Eau Fraiche 100ml TESTER"
    )
    report["right_catalogue_candidates"] = [
        "Estee Lauder AZUREE SOLEIL EAU FRAICHE SKINS... 0.00",
        "Le tage Me ee ees MN Cee Mi ee A eae 15.20",
        "Estee Lauder BRONZE GODDESS SOLEIL 100ml TESTER 0.00",
    ]
    output = review_ginger_cpu_ocr_report(report)
    assert [item["status"] for item in output["candidates"]] == [
        "unreadable_ocr", "unreadable_ocr", "possible_ocr_match_review",
    ]
    assert output["candidates"][0]["reward_eligible"] is False
    assert output["candidates"][1]["reward_eligible"] is False
    assert output["candidates"][2]["reward_eligible"] is True
    assert output["safe_for_automatic_matching"] is False


def test_full_clear_non_tester_and_tester_are_distinct():
    report = real_world_report()
    report["selected_row_visual_candidate"] = "Estee Lauder Bronze Goddess EDP 100ml TESTER"
    report["right_catalogue_candidates"] = ["Estee Lauder Bronze Goddess EDP 100ml"]
    output = review_ginger_cpu_ocr_report(report)
    assert output["candidates"][0]["status"] == "tester_mismatch"
    assert output["candidates"][0]["reward_eligible"] is True
