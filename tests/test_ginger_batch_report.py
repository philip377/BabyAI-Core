import json
import pytest
from test_ginger_batch import Backend
from babyai.ginger_batch import run_batch
from babyai.ginger_batch_report import render_batch_report
from babyai.ginger_price_queue import PriceQueueError


def test_completed_pass_summary_has_timing_and_no_approval(tmp_path):
    directory=tmp_path/"run"
    run_batch(Backend(),directory,2,confirmed=True)
    text=render_batch_report(directory)
    assert "2, 3" in text and "ограниченный проход завершён" in text
    assert "Общее время" in text and "не подтверждает сопоставление" in text
    assert text.count("результат записан") == 8


def test_failed_step_does_not_look_completed(tmp_path):
    backend=Backend();backend.fail="FilterStep"
    directory=tmp_path/"run"
    with pytest.raises(RuntimeError): run_batch(backend,directory,2,confirmed=True)
    text=render_batch_report(directory)
    assert "проход остановлен" in text and "исход действия требует проверки" in text
    assert "Строки с сохранёнными отчётами: нет" in text


def test_old_journal_does_not_invent_timing(tmp_path):
    directory=tmp_path/"run";run_batch(Backend(),directory,1,confirmed=True)
    for path in directory.glob("*-observed.json"):
        data=json.loads(path.read_text());data.pop("execution");path.write_text(json.dumps(data))
    path=directory/"result.json";data=json.loads(path.read_text());data.pop("timing");path.write_text(json.dumps(data))
    text=render_batch_report(directory)
    assert "общее время не измерялось" in text


def test_incomplete_run_cannot_be_reported_as_finished(tmp_path):
    directory=tmp_path/"run";directory.mkdir()
    with pytest.raises(PriceQueueError): render_batch_report(directory)


def test_missing_observed_action_rejected(tmp_path):
    directory=tmp_path/"run";run_batch(Backend(),directory,1,confirmed=True)
    (directory/"action-001-observed.json").unlink()
    with pytest.raises(PriceQueueError): render_batch_report(directory)
