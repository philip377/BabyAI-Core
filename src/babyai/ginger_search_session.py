"""Reconcile a saved filter intent with two read-only observations; never replay input."""
import argparse
import json
from pathlib import Path
from .ginger_search_transition import _context
from .ginger_price_queue import PriceQueueError
from .ginger_candidate_review import review_snapshot


def reconcile_search(intent, first, second, process_id):
    a, b = _context(first), _context(second)
    if a != b:
        raise PriceQueueError("Search observations changed; wait and observe again without input")
    if (type(process_id) is not int or process_id <= 0 or intent.get("process_id") != process_id
            or type(intent.get("process_id")) is not int or intent.get("can_write") is not False
            or type(intent.get("enabled")) is not bool):
        raise PriceQueueError("Invalid intent or changed process")
    before = intent.get("before", {})
    current = second["SearchWords"]
    keys = ("TableHandle", "ObservedRowPosition", "DocumentNumber", "Supplier", "Title", "PriceText", "LinkedProductText")
    if (not isinstance(before, dict) or before.get("PanelHandle") != current["PanelHandle"]
            or not isinstance(before.get("Source"), dict)
            or any(before["Source"].get(k) != current["Source"][k] for k in keys)):
        raise PriceQueueError("Selected source or search panel differs from saved intent")
    rows = before.get("Candidates")
    if not isinstance(rows, list) or len(rows) != len(current["Candidates"]):
        raise PriceQueueError("Search words changed")
    found = 0
    for old, now in zip(rows, current["Candidates"]):
        if any(old.get(k) != now[k] for k in ("ObservedPosition", "Title", "DescriptionText")):
            raise PriceQueueError("Search words changed")
        if now["Title"] == intent.get("word"):
            found += 1
            desired = intent["enabled"]
            if desired:
                valid = now.get("StateImage") == 2 and now["Checked"] is True
            else:
                valid = type(now.get("StateImage")) is int and now["StateImage"] in (0, 1) and now["Checked"] is not True
            if not valid:
                raise PriceQueueError("Desired filter state not observed; do not replay input")
        elif old.get("StateImage") != now.get("StateImage") or old.get("Checked") != now["Checked"]:
            raise PriceQueueError("Another filter changed")
    if found != 1:
        raise PriceQueueError("Intent word must occur exactly once")
    # _context compares content, not native state images. Check those separately.
    if [r.get("StateImage") for r in first["SearchWords"]["Candidates"]] != [r.get("StateImage") for r in current["Candidates"]]:
        raise PriceQueueError("Filter state images changed")
    return {"status": "observed_stable_needs_review", "observations_agree": True,
            "filter_state_verified": True, "automatic_retry_allowed": False,
            "search_completion_verified": False, "can_write": False,
            "review": review_snapshot(second["Products"])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("intent", "first", "second"):
        parser.add_argument(name, type=Path)
    parser.add_argument("--process-id", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    values = []
    for path in (args.intent, args.first, args.second):
        if path.stat().st_size > 2_000_000:
            raise ValueError("Observation too large")
        values.append(json.loads(path.read_text(encoding="utf-8-sig")))
    result = reconcile_search(*values, args.process_id)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)


if __name__ == "__main__":
    main()
