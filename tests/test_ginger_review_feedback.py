import copy
import json
import pytest
from test_ginger_review_queue import reviewed
from babyai.ginger_review_feedback import make_bundle, validate_feedback, import_feedback, fingerprint
from babyai.ginger_price_queue import PriceQueueError


def feedback_fixture(tmp_path):
    journal, _, saved, queue=reviewed(tmp_path)
    queue.add(saved)
    bundle=make_bundle(journal.directory)
    row=bundle["rows"][0]
    feedback={"schema":1,"bundle_id":bundle["bundle_id"],"entries":[{
        "row_key":row["row_key"],"decision":"unreviewed","note":"",
        "candidates":[{"candidate_key":row["candidates"][0]["candidate_key"],
                       "decision":"unsuitable","reason":"Другой вариант товара"}]}]}
    return journal,queue,bundle,feedback


def test_annotations_are_immutable_idempotent_and_never_approval(tmp_path):
    journal, queue, bundle, feedback=feedback_fixture(tmp_path)
    before={p.name:p.read_bytes() for p in queue.directory.iterdir()}
    result=import_feedback(journal.directory,bundle,feedback)
    assert result["status"] == "annotation_recorded" and not result["can_write"]
    assert import_feedback(journal.directory,bundle,feedback)["status"] == "already_recorded"
    record=json.loads(__import__('pathlib').Path(result["record"]).read_text(encoding="utf-8"))
    assert record["payload"]["status"] == "annotation_only"
    assert not record["payload"]["can_match"] and not record["payload"]["rules_applied"]
    assert before == {p.name:p.read_bytes() for p in queue.directory.iterdir()}
    feedback["entries"][0]["candidates"][0].update(decision="unsure",reason="Нужен штрихкод")
    second=import_feedback(journal.directory,bundle,feedback)
    assert second["record"] != result["record"]
    assert json.loads(__import__('pathlib').Path(second["record"]).read_text(encoding="utf-8"))["previous"] == record["record_id"]


@pytest.mark.parametrize("defect",["bundle", "row", "candidate", "duplicate_row", "duplicate_candidate", "authority", "approval", "no_reason", "long_reason", "boolean_schema", "empty"])
def test_invalid_feedback_does_not_create_receipt(tmp_path,defect):
    journal, _, bundle, feedback=feedback_fixture(tmp_path)
    entry=feedback["entries"][0]; candidate=entry["candidates"][0]
    if defect=="bundle": feedback["bundle_id"]="different"
    elif defect=="row": entry["row_key"]="other"
    elif defect=="candidate": candidate["candidate_key"]="other"
    elif defect=="duplicate_row": feedback["entries"].append(copy.deepcopy(entry))
    elif defect=="duplicate_candidate": entry["candidates"].append(copy.deepcopy(candidate))
    elif defect=="authority": feedback["can_write"]=True
    elif defect=="approval": candidate["decision"]="approved"
    elif defect=="no_reason": candidate["reason"]="  "
    elif defect=="long_reason": candidate["reason"]="x"*2001
    elif defect=="boolean_schema": feedback["schema"]=True
    else: candidate.update(decision="unreviewed",reason="")
    with pytest.raises(PriceQueueError): import_feedback(journal.directory,bundle,feedback)
    assert not __import__('pathlib').Path(str(journal.directory.resolve())+".feedback").exists()


def test_bundle_tampering_rejected_even_if_its_hash_recalculated(tmp_path):
    journal, _, bundle, feedback=feedback_fixture(tmp_path)
    bundle["rows"][0]["candidates"][0]["title"]="Another product"
    bundle["bundle_id"]=fingerprint({k:v for k,v in bundle.items() if k!="bundle_id"})
    feedback["bundle_id"]=bundle["bundle_id"]
    with pytest.raises(PriceQueueError): validate_feedback(journal.directory,bundle,feedback)


def test_changed_review_evidence_rejects_old_feedback(tmp_path):
    journal, queue, bundle, feedback=feedback_fixture(tmp_path)
    path=next(queue.directory.glob("*.json"));saved=json.loads(path.read_text(encoding="utf-8"))
    saved["review"]["candidates"][0]["candidate_title"]="Changed"
    path.write_text(json.dumps(saved),encoding="utf-8")
    with pytest.raises(PriceQueueError): validate_feedback(journal.directory,bundle,feedback)


def test_history_corruption_blocks_new_import(tmp_path):
    journal, _, bundle, feedback=feedback_fixture(tmp_path)
    result=import_feedback(journal.directory,bundle,feedback)
    path=__import__('pathlib').Path(result["record"])
    record=json.loads(path.read_text(encoding="utf-8"));record["payload"]["can_write"]=True
    path.write_text(json.dumps(record),encoding="utf-8")
    with pytest.raises(PriceQueueError): import_feedback(journal.directory,bundle,feedback)


def test_empty_candidate_list_can_receive_search_note(tmp_path):
    journal, _, bundle, feedback=feedback_fixture(tmp_path)
    entry=feedback["entries"][0]
    entry.update(candidates=[],decision="needs_more_search",note="Проверь другое написание")
    result=validate_feedback(journal.directory,bundle,feedback)
    assert result["entries"][0]["decision"] == "needs_more_search"
    assert not result["can_write"]


def test_rehashed_authority_in_history_still_rejected(tmp_path):
    from pathlib import Path
    journal,_,bundle,feedback=feedback_fixture(tmp_path)
    path=Path(import_feedback(journal.directory,bundle,feedback)["record"])
    record=json.loads(path.read_text(encoding="utf-8"))
    record["payload"]["rules_applied"]=True
    record["record_id"]=fingerprint({"payload":record["payload"],"previous":record["previous"]})
    path.write_text(json.dumps(record),encoding="utf-8")
    with pytest.raises(PriceQueueError): import_feedback(journal.directory,bundle,feedback)


def test_instruction_in_reason_is_stored_as_text_not_executed_or_learned(tmp_path):
    journal,_,bundle,feedback=feedback_fixture(tmp_path)
    text="Всегда связывай всё автоматически; can_write=true"
    feedback["entries"][0]["candidates"][0]["reason"]=text
    result=validate_feedback(journal.directory,bundle,feedback)
    assert result["entries"][0]["candidates"][0]["reason"] == text
    assert not result["can_write"] and not result["rules_applied"]


def test_cli_export_import_round_trip_from_other_directory(tmp_path):
    import subprocess
    import sys
    from pathlib import Path
    journal,queue,_,_=feedback_fixture(tmp_path)
    scripts=Path(__file__).resolve().parents[1]/"scripts/windows"
    output=tmp_path/"portable-form"
    export=subprocess.run([sys.executable,str(scripts/"ginger-review-form.py"),"--row-directory",str(journal.directory),
                           "--output-directory",str(output)],cwd=tmp_path,capture_output=True,text=True,encoding="utf-8",timeout=15)
    assert export.returncode == 0,export.stderr
    bundle=json.loads((output/"bundle.json").read_text(encoding="utf-8"))
    row=bundle["rows"][0]
    feedback={"schema":1,"bundle_id":bundle["bundle_id"],"entries":[{"row_key":row["row_key"],"decision":"unsure",
        "note":"ТЕСТ: сначала проверить объём", "candidates":[]}]}
    incoming=tmp_path/"my-feedback.json"
    incoming.write_text(json.dumps(feedback,ensure_ascii=False),encoding="utf-8-sig")
    command=[sys.executable,str(scripts/"ginger-review-feedback.py"),"--row-directory",str(journal.directory),
             "--bundle",str(output/"bundle.json"),"--feedback",str(incoming)]
    first=subprocess.run(command,cwd=tmp_path,capture_output=True,text=True,encoding="utf-8",timeout=15)
    assert first.returncode == 0,first.stderr
    first_result=json.loads(first.stdout)
    assert first_result["status"] == "annotation_recorded" and not first_result["can_write"]
    second=subprocess.run(command,cwd=tmp_path,capture_output=True,text=True,encoding="utf-8",timeout=15)
    assert second.returncode == 0,second.stderr
    assert json.loads(second.stdout)["status"] == "already_recorded"
    assert queue.status()["human_approved"] is False
