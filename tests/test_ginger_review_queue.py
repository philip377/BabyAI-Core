import copy
import json
import pytest
from test_ginger_row_review import setup
from babyai.ginger_row_review import finish_row_review
from babyai.ginger_review_queue import ReviewQueue
from babyai.ginger_price_queue import PriceQueueError


def reviewed(tmp_path):
    journal, folder, report = setup(tmp_path)
    finish_row_review(journal.directory, folder, report)
    saved=json.loads((folder/"row-review.json").read_text(encoding="utf-8"))
    return journal, report, saved, ReviewQueue(journal.directory)


def test_sample_alone_does_not_open_queue_gate(tmp_path):
    journal, _, _, queue = reviewed(tmp_path)
    assert journal.status()["selected_sampled"]
    with pytest.raises(PriceQueueError): queue.gate_next()


def test_queue_idempotent_and_allows_only_next_row(tmp_path):
    journal, report, saved, queue = reviewed(tmp_path)
    queue.add(saved); assert queue.add(saved)["needs_manual_review"] == 1
    assert len(list(queue.directory.iterdir())) == 1
    assert queue.gate_next()["to_position"] == 2
    assert not queue.status()["human_approved"]
    journal.begin(report["rows_report"])
    with pytest.raises(PriceQueueError): queue.gate_next()


@pytest.mark.parametrize("defect", ["hash", "position", "permission", "approved", "absence", "candidate_permission"])
def test_forged_review_rejected(tmp_path, defect):
    _, _, saved, queue = reviewed(tmp_path)
    if defect == "hash": saved["sample_sha"]="0"*64
    elif defect == "position": saved["source_position"]=True
    elif defect == "permission": saved["can_write"]=True
    elif defect == "approved": saved["status"]="approved"
    elif defect == "absence": saved["review"]["absence_proven"]=True
    else: saved["review"]["candidates"][0]["can_match"]=True
    with pytest.raises(PriceQueueError): queue.add(saved)
    assert not queue.directory.exists()


def test_changed_queued_report_never_overwritten(tmp_path):
    _, _, saved, queue = reviewed(tmp_path); queue.add(saved)
    before=(queue.directory/"row-0001.json").read_bytes()
    saved["review"]["document_number"]="changed"
    with pytest.raises(PriceQueueError): queue.add(saved)
    assert (queue.directory/"row-0001.json").read_bytes()==before


def test_partial_queue_blocks_gate(tmp_path):
    _, _, saved, queue = reviewed(tmp_path); queue.add(saved)
    (queue.directory/"row-0001.json").write_text('{')
    with pytest.raises(ValueError): queue.gate_next()


def test_windows_next_reviewed_stops_before_ui_without_queue(tmp_path):
    import os
    import subprocess
    from pathlib import Path
    if os.name != "nt": pytest.skip("Windows controller guard")
    journal, _, _, _ = reviewed(tmp_path)
    root=Path(__file__).resolve().parents[1]
    shell=Path(os.environ["WINDIR"])/"System32/WindowsPowerShell/v1.0/powershell.exe"
    result=subprocess.run([str(shell),"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass",
        "-File",str(root/"scripts/windows/ginger-row-step.ps1"),"-Operation","NextReviewed",
        "-QueueDirectory",str(journal.directory),"-ConfirmNavigation"],capture_output=True,timeout=20)
    assert result.returncode != 0
    assert b"Current review not queued" in result.stderr
    assert journal.status()["pending_attempt"] is None
    assert journal.status()["selected_position"] == 1


@pytest.mark.parametrize("defect",["candidate_position","candidate_title","explanation","search_count","search_authority","search_flag","preserved_words"])
def test_malformed_explanations_and_search_comparison_rejected(tmp_path,defect):
    _,_,saved,queue=reviewed(tmp_path)
    candidate=saved["review"]["candidates"][0]
    if defect=="candidate_position": candidate["observed_position"]=True
    elif defect=="candidate_title": candidate["candidate_title"]=""
    elif defect=="explanation": candidate["differences"]=[{"field":"x","message":{"script":"bad"}}]
    elif defect=="search_count": saved["search_observation"]["after_count"]=0
    elif defect=="search_authority": saved["search_observation"]["automatic_broadening_allowed"]=True
    elif defect=="search_flag": saved["search_observation"]["possible_overfiltering"]=True
    else: saved["search_observation"]["preserved_verification_filters"]=["unlisted"]
    with pytest.raises(PriceQueueError): queue.add(saved)
    assert not queue.directory.exists()


@pytest.mark.parametrize("number",[float("nan"),float("inf"),float("-inf")])
def test_nonfinite_explanation_values_rejected_before_form_export(tmp_path,number):
    _,_,saved,queue=reviewed(tmp_path)
    saved["review"]["candidates"][0]["differences"]=[{"field":"volume","message":"Объём","source":number,"candidate":50}]
    with pytest.raises(PriceQueueError): queue.add(saved)
