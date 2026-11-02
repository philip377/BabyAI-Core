import pytest
from test_ginger_review_queue import reviewed
from babyai.ginger_review_report import render_report, _text
from babyai.ginger_price_queue import PriceQueueError


def test_report_has_source_uncertainty_and_no_approval(tmp_path):
    journal, _, saved, queue = reviewed(tmp_path)
    queue.add(saved)
    before = {p.name:p.read_bytes() for p in queue.directory.iterdir()}
    text = render_report(journal.directory)
    assert "MARBERT MAN TOO" in text
    assert _text(saved["review"]["candidates"][0]["candidate_title"]) in text
    assert "Неопределённость" in text and "Различие" in text
    assert "Совпадение товара не подтверждено" in text
    assert before == {p.name:p.read_bytes() for p in queue.directory.iterdir()}


def test_pending_navigation_rejects_report(tmp_path):
    journal, report, saved, queue = reviewed(tmp_path)
    queue.add(saved); journal.begin(report["rows_report"])
    with pytest.raises(PriceQueueError): render_report(journal.directory)


def test_corrupt_queue_rejected(tmp_path):
    journal, _, saved, queue = reviewed(tmp_path)
    queue.add(saved)
    (queue.directory/"row-0001.json").write_text("{}")
    with pytest.raises(PriceQueueError): render_report(journal.directory)


def test_supplier_text_cannot_inject_links_or_html():
    text = _text("[click](https://example.com) <img>\n# heading")
    assert "[click](" not in text and "<img>" not in text
    assert "\n" not in text


def test_cli_preserves_existing_report(tmp_path):
    import subprocess
    import sys
    from pathlib import Path
    journal, _, saved, queue = reviewed(tmp_path)
    queue.add(saved)
    output=tmp_path/"report.md"
    script=Path(__file__).resolve().parents[1]/"scripts/windows/ginger-review-report.py"
    args=[sys.executable,str(script),"--row-directory",str(journal.directory),"--output",str(output)]
    first=subprocess.run(args,capture_output=True,cwd=tmp_path)
    assert first.returncode == 0, first.stderr
    original=output.read_bytes()
    assert subprocess.run(args,capture_output=True,cwd=tmp_path).returncode != 0
    assert output.read_bytes() == original
