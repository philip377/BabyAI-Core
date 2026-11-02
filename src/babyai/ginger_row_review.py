"""Finish a row's read-only review after its exact prepared filter plan is observed."""
import argparse
import hashlib
import json
from pathlib import Path
from .ginger_filter_plan import FilterPlan, _query, _read, _save
from .ginger_price_queue import PriceQueueError
from .ginger_row_journal import RowNavigationJournal, snapshot
from .ginger_row_filter_prepare import propose_filters
from .ginger_search_transition import _context
from .ginger_candidate_review import review_snapshot


def finish_row_review(row_directory, preparation_directory, report):
    folder = Path(preparation_directory)
    preparation = _read(folder / "prepare-filters.json")
    original = preparation["context"]
    first, second = report["first"], report["second"]
    if _context(first) != _context(second) or _query(first) != _query(second):
        raise PriceQueueError("Search changed between review observations")
    journal = RowNavigationJournal(row_directory)
    digest, selected, count, pending, sequence, sampled, _, _ = journal._state()
    fresh = report["rows_report"]
    if pending is not None or snapshot(fresh) != (digest, selected, count):
        raise PriceQueueError("Pending navigation or changed row table")
    if snapshot(preparation["rows_report"]) != (digest, selected, count):
        raise PriceQueueError("Preparation belongs to another row table")
    expected_source = fresh["rows"][selected-1]["Observation"]
    keys = ("TableHandle", "ObservedRowPosition", "DocumentNumber", "Supplier", "Title", "PriceText", "LinkedProductText")
    if any(second["Products"]["Source"][key] != expected_source[key] for key in keys):
        raise PriceQueueError("Review source does not match selected row")
    proposed = propose_filters(original)
    if proposed["steps"]:
        plan, final_context, completed, plan_pending, _ = FilterPlan(folder / "filter-plan")._state()
        if (plan_pending is not None or completed != len(plan["steps"])
                or plan["steps"] != proposed["steps"] or plan["process_id"] != fresh["process_id"]
                or _query(plan["context"]) != _query(original) or _query(final_context) != _query(second)):
            raise PriceQueueError("Prepared filters are pending, different or no longer observed")
    elif _query(original) != _query(second):
        raise PriceQueueError("Filters changed since no-action preparation")
    review = review_snapshot(second["Products"])
    sample = second["Products"]
    sample_sha = hashlib.sha256(json.dumps(sample, sort_keys=True, ensure_ascii=False).encode("utf-8")).hexdigest()
    result = {"source_position": selected, "row_snapshot_digest": digest, "sample_sha": sample_sha,
              "status": "needs_manual_review", "review": review, "can_write": False,
              "products_processed": False, "search_completion_verified": False}
    output = folder / "row-review.json"
    # An interrupted publication can resume only with exactly the same evidence.
    if output.exists():
        if _read(output) != result:
            raise PriceQueueError("Existing row review differs; do not overwrite or skip")
    elif sampled:
        raise PriceQueueError("Row already sampled outside this review")
    else:
        _save(output, result)
    if sampled:
        event = _read(Path(row_directory) / f"{sequence:06d}.json")
        if event.get("kind") != "sample" or event.get("sample_sha") != sample_sha:
            raise PriceQueueError("Existing sample does not match row review")
    else:
        journal.record_sample(dict(fresh, candidate_snapshot=sample))
    return {"report": str(output), "row_status": journal.status(), "status": "needs_manual_review",
            "can_write": False, "automatic_next_row": False, "products_processed": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--row-directory", required=True, type=Path)
    parser.add_argument("--preparation-directory", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(finish_row_review(args.row_directory, args.preparation_directory, _read(args.report)), ensure_ascii=False))


if __name__ == "__main__": main()
