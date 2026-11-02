"""Snapshot-bound human annotations. Never approval, matching, or automatic learning."""
import argparse
import hashlib
import json
from pathlib import Path
from .ginger_batch import batch_lock
from .ginger_filter_plan import _read, _save
from .ginger_review_queue import ReviewQueue
from .ginger_price_queue import PriceQueueError


def fingerprint(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":")).encode("utf-8")).hexdigest()


def review_rows(row_directory):
    queue = ReviewQueue(row_directory)
    state, _, queued = queue._validated()
    if state[3] is not None: raise PriceQueueError("Pending row navigation")
    source_rows = _read(queue.journal.directory / "plan.json")["rows"]
    rows = []
    for position, report in sorted(queued.items()):
        source = source_rows[position-1]["Observation"]
        row_key = fingerprint(report)
        candidates = []
        for index, candidate in enumerate(report["review"]["candidates"], 1):
            candidates.append({"candidate_key": fingerprint([row_key, index, candidate["candidate_title"]]),
                               "position": index, "title": candidate["candidate_title"],
                               "differences": candidate["differences"], "uncertainties": candidate["uncertainties"]})
        rows.append({"row_key": row_key, "position": position, "title": source["Title"],
                     "supplier": source["Supplier"], "document": source["DocumentNumber"],
                     "price_text": source["PriceText"], "candidates": candidates})
    return rows


def make_bundle(row_directory):
    body = {"schema": 1, "rows": review_rows(row_directory), "can_write": False, "can_match": False}
    bundle = dict(body, bundle_id=fingerprint(body))
    if len(json.dumps(bundle,ensure_ascii=False,indent=2).encode("utf-8")) > 2_000_000:
        raise PriceQueueError("Review bundle too large; export a smaller session")
    return bundle


def validate_bundle(row_directory, bundle):
    if not isinstance(bundle, dict) or set(bundle) != {"schema", "rows", "can_write", "can_match", "bundle_id"}:
        raise PriceQueueError("Invalid review bundle")
    body = {key:value for key,value in bundle.items() if key != "bundle_id"}
    if (type(bundle["schema"]) is not int or bundle["schema"] != 1 or bundle["can_write"] is not False
            or bundle["can_match"] is not False or fingerprint(body) != bundle["bundle_id"]
            or not isinstance(bundle["rows"], list)):
        raise PriceQueueError("Invalid review bundle fingerprint or permission")
    current = {row["row_key"]:row for row in review_rows(row_directory)}
    seen = set()
    for row in bundle["rows"]:
        if not isinstance(row, dict) or not isinstance(row.get("row_key"), str):
            raise PriceQueueError("Invalid bundle row")
        key = row["row_key"]
        if key in seen or current.get(key) != row:
            raise PriceQueueError("Review changed since export")
        seen.add(key)
    return {row["row_key"]:row for row in bundle["rows"]}


def _note(value):
    if not isinstance(value, str) or len(value) > 2000 or any(ord(c)<32 and c not in "\n\r\t" for c in value):
        raise PriceQueueError("Annotation must be bounded text")
    return value.strip()


def validate_feedback(row_directory, bundle, feedback):
    rows = validate_bundle(row_directory, bundle)
    if (not isinstance(feedback, dict) or set(feedback) != {"schema", "bundle_id", "entries"}
            or type(feedback["schema"]) is not int or feedback["schema"] != 1
            or feedback["bundle_id"] != bundle["bundle_id"] or not isinstance(feedback["entries"], list)
            or len(feedback["entries"]) > len(rows)):
        raise PriceQueueError("Feedback belongs to another bundle or has unsupported fields")
    clean, seen = [], set()
    for entry in feedback["entries"]:
        if not isinstance(entry, dict) or set(entry) != {"row_key", "decision", "note", "candidates"}:
            raise PriceQueueError("Invalid feedback row")
        key = entry["row_key"]
        if not isinstance(key, str) or key not in rows or key in seen:
            raise PriceQueueError("Unknown or duplicate feedback row")
        seen.add(key)
        if entry["decision"] not in ("unreviewed", "skip", "unsure", "needs_more_search"):
            raise PriceQueueError("Row decision cannot authorize a write")
        note = _note(entry["note"])
        candidates = {item["candidate_key"]:item for item in rows[key]["candidates"]}
        if not isinstance(entry["candidates"], list) or len(entry["candidates"]) > len(candidates):
            raise PriceQueueError("Invalid candidate annotations")
        annotations, candidate_seen = [], set()
        for item in entry["candidates"]:
            if not isinstance(item, dict) or set(item) != {"candidate_key", "decision", "reason"}:
                raise PriceQueueError("Invalid candidate annotation")
            candidate_key = item["candidate_key"]
            if not isinstance(candidate_key, str) or candidate_key not in candidates or candidate_key in candidate_seen:
                raise PriceQueueError("Unknown or duplicate candidate")
            candidate_seen.add(candidate_key)
            if item["decision"] not in ("unreviewed", "suitable", "unsuitable", "unsure"):
                raise PriceQueueError("Candidate annotation is not approval")
            reason = _note(item["reason"])
            if item["decision"] in ("suitable", "unsuitable") and not reason:
                raise PriceQueueError("Explain why the candidate is suitable or unsuitable")
            if item["decision"] != "unreviewed" or reason:
                annotations.append(dict(item, reason=reason))
        if entry["decision"] != "unreviewed" or note or annotations:
            clean.append(dict(entry, note=note, candidates=annotations))
    if not clean: raise PriceQueueError("No human annotations to import")
    return {"schema":1, "bundle_id":bundle["bundle_id"], "entries":clean,
            "status":"annotation_only", "can_write":False, "can_match":False, "rules_applied":False}


def import_feedback(row_directory, bundle, feedback):
    # Caller holds batch_lock; observations may be old, but their exact receipt must still exist.
    payload = validate_feedback(row_directory, bundle, feedback)
    directory = Path(str(Path(row_directory).resolve()) + ".feedback")
    directory.mkdir(parents=True, exist_ok=True)
    files = sorted(directory.iterdir())
    prior = None
    duplicate = None
    for index, path in enumerate(files):
        if path.name != f"{index:06d}.json": raise PriceQueueError("Invalid feedback history")
        record = _read(path)
        if (not isinstance(record,dict) or set(record) != {"payload", "previous", "record_id"}
                or record["previous"] != prior or record["record_id"] != fingerprint({"payload":record["payload"], "previous":prior})):
            raise PriceQueueError("Feedback history corrupted")
        old = record["payload"]
        if (not isinstance(old,dict) or old.get("status") != "annotation_only"
                or any(old.get(key) is not False for key in ("can_write","can_match","rules_applied"))):
            raise PriceQueueError("Feedback history contains unsupported authority")
        prior = record["record_id"]
        if record["payload"] == payload: duplicate = path
    if duplicate is not None: return {"record":str(duplicate), "status":"already_recorded", "can_write":False}
    body = {"payload":payload, "previous":prior}
    path = directory/f"{len(files):06d}.json"
    _save(path, dict(body, record_id=fingerprint(body)))
    return {"record":str(path), "status":"annotation_recorded", "can_write":False, "rules_applied":False}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--row-directory",type=Path,required=True)
    parser.add_argument("--bundle",type=Path,required=True)
    parser.add_argument("--feedback",type=Path,required=True)
    args=parser.parse_args()
    with batch_lock(args.row_directory):
        result=import_feedback(args.row_directory,_read(args.bundle),_read(args.feedback))
    print(json.dumps(result,ensure_ascii=False))


if __name__ == "__main__": main()
