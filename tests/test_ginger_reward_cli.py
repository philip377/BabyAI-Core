from __future__ import annotations

import json
from typer.testing import CliRunner

from babyai.ginger_reward_cli import app


def sample(tmp_path):
    path = tmp_path / "ginger-cpu-report.json"
    path.write_text(json.dumps({
        "source": "Ginger4 CPU OCR pilot",
        "read_only": True, "ginger_modified": False, "local_only": True,
        "selected_row_color_band_detected": True,
        "selected_supplier_row_verified": False,
        "selected_row_visual_candidate": "Bois 1920 Oro Rosso edp 100 mi",
        "right_catalogue_candidates": [
            "Bois 1920 ОВО ROSSO edp 100ml 82,00",
            "Bois 1920 ORO ROSSO edp 100ml (тестер) 0.00"
        ],
    }, ensure_ascii=False), encoding="utf-8")
    return path


def test_ocr_preview_never_awards_points(tmp_path):
    runner = CliRunner()
    report = sample(tmp_path)
    result = runner.invoke(app, ["preview", str(report)])
    assert result.exit_code == 0, result.output
    payload = json.loads(result.output)
    assert payload["points_awarded"] == 0
    assert payload["reward_verification"] == "unverified"
    assert payload["safe_for_automatic_matching"] is False
    assert not list(tmp_path.glob("*.sqlite3"))


def test_record_and_status_require_operator_and_evidence(tmp_path):
    runner = CliRunner()
    db = tmp_path / "score.sqlite3"
    args = [
        "record", "--attempt-id", "case-1", "--session-id", "trial-1",
        "--supplier-row-key", "supplier-1", "--catalog-item-key", "sku-1",
        "--decision", "reject", "--outcome", "correct_reject",
        "--verifier", "operator", "--evidence-reference", "manual-confirmation-1",
        "--db", str(db),
    ]
    response = runner.invoke(app, args)
    assert response.exit_code == 0, response.output
    result = json.loads(response.output)
    assert result["awarded"] == 5
    assert result["status"]["points"] == 5
    assert result["status"]["automatic_ginger_write_allowed"] is False
    status = runner.invoke(app, ["status", "--session-id", "trial-1", "--db", str(db)])
    assert status.exit_code == 0, status.output
    assert json.loads(status.output)["points"] == 5
    again = runner.invoke(app, args)
    assert again.exit_code == 1
    assert "Duplicate" in again.output


def test_agent_cannot_self_verify_reward(tmp_path):
    runner = CliRunner()
    response = runner.invoke(app, [
        "record", "--attempt-id", "case-1", "--session-id", "trial-1",
        "--supplier-row-key", "supplier-1", "--catalog-item-key", "sku-1",
        "--decision", "link", "--outcome", "correct_link",
        "--verifier", "unix", "--evidence-reference", "I think I did well",
        "--db", str(tmp_path / "points.sqlite3"),
    ])
    assert response.exit_code == 1
    assert "cannot verify its own work" in response.output
