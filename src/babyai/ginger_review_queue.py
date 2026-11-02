"""Immutable manual-review inbox tied to validated row sample receipts. No UI input."""
import argparse
import json
import re
from pathlib import Path
from .ginger_filter_plan import _read, _save
from .ginger_row_journal import RowNavigationJournal
from .ginger_price_queue import PriceQueueError


class ReviewQueue:
    def __init__(self, row_directory):
        self.journal = RowNavigationJournal(row_directory)
        self.directory = Path(str(Path(row_directory)) + ".reviews")

    def _validated(self):
        state = self.journal._state()
        samples = {}
        for path in sorted(self.journal.directory.glob("[0-9]*.json")):
            event = _read(path)
            if event.get("kind") == "sample": samples[event["target"]] = event["sample_sha"]
        queued = {}
        if self.directory.exists():
            for path in sorted(self.directory.iterdir()):
                if not re.fullmatch(r"row-[0-9]{4}\.json", path.name):
                    raise PriceQueueError("Unknown review queue entry")
                report = _read(path)
                position = self._validate_report(report, state[0], samples)
                if path.name != f"row-{position:04d}.json" or position in queued:
                    raise PriceQueueError("Invalid review order or duplicate")
                queued[position] = report
        return state, samples, queued

    def _validate_report(self, report, digest, samples):
        if not isinstance(report, dict): raise PriceQueueError("Invalid row review")
        position = report.get("source_position")
        if (type(position) is not int or position not in samples or report.get("row_snapshot_digest") != digest
                or report.get("sample_sha") != samples[position] or report.get("status") != "needs_manual_review"
                or any(report.get(k) is not False for k in ("can_write","products_processed","search_completion_verified"))):
            raise PriceQueueError("Review does not match a recorded row sample")
        review = report.get("review")
        if (not isinstance(review, dict) or type(review.get("source_position")) is not int or review.get("source_position") != position
                or any(review.get(k) is not False for k in ("can_write","can_match","absence_proven","search_completion_verified"))
                or not isinstance(review.get("candidates"), list) or len(review["candidates"]) > 256):
            raise PriceQueueError("Invalid read-only review evidence")
        original = _read(self.journal.directory / "plan.json")["rows"][position-1]["Observation"]
        if review.get("document_number") != original["DocumentNumber"]:
            raise PriceQueueError("Review document differs from journal")
        for item in review["candidates"]:
            if (not isinstance(item, dict) or item.get("source_title") != original["Title"] or item.get("status") != "manual_review"
                    or any(item.get(k) is not False for k in ("can_write","can_match","identity_verified","search_completion_verified"))):
                raise PriceQueueError("Unsupported candidate authorization")
        return position

    def add(self, report):
        state, samples, queued = self._validated()
        position = self._validate_report(report, state[0], samples)
        if state[3] is not None or position != state[1] or not state[5]:
            raise PriceQueueError("Only the current sampled row can be queued")
        if position in queued:
            if queued[position] != report: raise PriceQueueError("Existing queued review differs")
        else:
            self.directory.mkdir(parents=True, exist_ok=True)
            _save(self.directory / f"row-{position:04d}.json", report)
        return self.status()

    def status(self):
        state, _, queued = self._validated()
        return {"queued_positions": sorted(queued), "needs_manual_review": len(queued),
                "current_position": state[1], "current_review_queued": state[1] in queued,
                "pending_navigation": state[3] is not None, "can_write": False,
                "products_processed": False, "human_approved": False}

    def gate_next(self):
        state, _, queued = self._validated()
        if state[3] is not None or not state[5] or state[1] not in queued:
            raise PriceQueueError("Current review must be queued before next row")
        if state[1] >= state[2]: raise PriceQueueError("Last row; no wrap")
        return {"from_position": state[1], "to_position": state[1]+1,
                "can_write": False, "products_processed": False, "human_approved": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("add","status","gate-next"))
    parser.add_argument("--row-directory", required=True, type=Path)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    queue = ReviewQueue(args.row_directory)
    if args.operation == "add":
        if args.report is None: parser.error("--report required")
        result = queue.add(_read(args.report))
    else: result = queue.status() if args.operation == "status" else queue.gate_next()
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__": main()
