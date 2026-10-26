"""Contracts for bringing the read-only Ginger pilot into the real UNIX UI."""
from __future__ import annotations

from pathlib import Path

import pytest

from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommandError, DesktopCommands


def _write_local_report(user: Path) -> Path:
    folder = user / "Downloads" / "ginger-cpu-test-20261026-152808"
    folder.mkdir(parents=True)
    report = folder / "ginger-cpu-report.json"
    import json
    report.write_text(json.dumps({
        "source": "Ginger4 CPU OCR pilot",
        "read_only": True, "ginger_modified": False, "local_only": True,
        "selected_row_color_band_detected": True,
        "selected_supplier_row_verified": False,
        "selected_row_visual_candidate":
            "Estee Lauder Bronze Goddess Soleil Eau Fraiche 100ml TESTER",
        "right_catalogue_candidates": [
            "Estee Lauder AZUREE SOLEIL EAU FRAICHE SKINS... 0.00",
            "Le tage Me ee ees MN Cee Mi ee A eae 15.20",
        ],
    }), encoding="utf-8")
    return report


def test_bridge_ginger_review_is_read_only_and_does_not_need_llm(
    tmp_path, monkeypatch,
):
    monkeypatch.setenv("USERPROFILE", str(tmp_path / "operator"))
    report = _write_local_report(tmp_path / "operator")
    commands = DesktopCommands(BabyAIConfig(
        data_dir=tmp_path / "core", provider="echo",
    ))
    result = commands.execute("ginger.preview", {"report_path": str(report)})
    assert result["ok"] is True
    review = result["review"]
    assert review["selected_supplier_as_read"].endswith("100ml TESTER")
    assert all(item["status"] == "unreadable_ocr"
               for item in review["candidates"])
    assert all(item["reward_eligible"] is False
               for item in review["candidates"])
    assert review["safe_for_automatic_matching"] is False
    assert "reward" not in result and "authorization" not in result
    assert "sku" not in result


@pytest.mark.parametrize("unsafe", [
    "/etc/passwd",
    "not-a-path",
    "C:/Windows/System32/config/SAM",
    "",
])
def test_ginger_bridge_refuses_arbitrary_file_paths(tmp_path, monkeypatch, unsafe):
    monkeypatch.setenv("USERPROFILE", str(tmp_path / "operator"))
    commands = DesktopCommands(BabyAIConfig(
        data_dir=tmp_path / "core", provider="echo",
    ))
    with pytest.raises(DesktopCommandError):
        commands.execute("ginger.preview", {"report_path": unsafe})


def test_ginger_bridge_refuses_reports_outside_fixed_downloads_subfolder(
    tmp_path, monkeypatch,
):
    user = tmp_path / "operator"
    monkeypatch.setenv("USERPROFILE", str(user))
    report = _write_local_report(user)
    other = tmp_path / "other"
    other.mkdir()
    moved = other / report.name
    moved.write_bytes(report.read_bytes())
    commands = DesktopCommands(BabyAIConfig(
        data_dir=tmp_path / "core", provider="echo",
    ))
    with pytest.raises(DesktopCommandError, match="Загрузок"):
        commands.execute("ginger.preview", {"report_path": str(moved)})


def test_native_ui_has_explicit_button_and_human_readable_local_logs():
    root = Path(__file__).resolve().parents[1]
    xaml = (root / "desktop/BabyAI.Desktop/MainWindow.xaml").read_text(encoding="utf-8")
    cs = (root / "desktop/BabyAI.Desktop/MainWindow.Ginger.cs").read_text(encoding="utf-8")
    bridge = (root / "desktop/BabyAI.Desktop/BabyAIBridgeClient.cs").read_text(encoding="utf-8")
    csproj = (root / "desktop/BabyAI.Desktop/BabyAI.Desktop.csproj").read_text(encoding="utf-8")
    adaptive = (root / "desktop/BabyAI.Desktop/MainWindow.AdaptiveUi.cs").read_text(encoding="utf-8")
    assert 'x:Name="GingerNavigationButton"' in xaml
    assert 'Click="GingerNavigationButton_Click"' in xaml
    assert 'GingerNavigationLabel.Visibility = labelVisibility' in adaptive
    assert "private async void GingerNavigationButton_Click" in cs
    assert "Проверить выделенный товар" in cs
    assert "Журнал действий" in cs or "журнал действий" in cs
    assert "ginger-actions.log" in cs
    assert "SaveGingerLogLine(entry)" in cs
    assert "-CaptureSupplierGrid" in cs
    assert 'ReadLineAsync(token)' in cs
    assert "ReviewGingerAsync(reportPath, token)" in cs
    assert '"ginger.preview"' in bridge
    assert "CopyToPublishDirectory" in csproj
    assert "test-ginger-cpu-ocr.ps1" in csproj
    assert "Баллы: 0" in cs
    assert "автоматическая привязка и создание товара выключены" in cs
    assert "IsAlwaysOnTop = false" in cs
    # No enabled GUI command for linking or changing Ginger records.
    assert "ginger.link" not in cs
    assert "SendInput" not in cs
    assert "mouse_event" not in cs
