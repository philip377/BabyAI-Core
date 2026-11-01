"""Durable ordered filter steps. Local receipts only; this module never sends UI input."""
import argparse
import copy
import json
import os
from pathlib import Path
from .ginger_price_queue import PriceQueueError
from .ginger_search_transition import _context
from .ginger_search_session import reconcile_search


def _save(path, value):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.flush()
        os.fsync(stream.fileno())


def _read(path):
    if path.stat().st_size > 2_000_000:
        raise PriceQueueError("Receipt too large")
    return json.loads(path.read_text(encoding="utf-8-sig"))


def _query(context):
    source, panels, contents = _context(context)
    images = []
    for row in context["SearchWords"]["Candidates"]:
        image = row.get("StateImage")
        if type(image) is not int or image not in (0, 1, 2):
            raise PriceQueueError("Unknown filter state")
        if (image == 2) != (row["Checked"] is True):
            raise PriceQueueError("Conflicting filter state")
        images.append(image)
    return source, panels, contents[0], tuple(images)


def _validate(plan):
    if plan.get("schema") != 1 or type(plan.get("process_id")) is not int or plan["process_id"] <= 0:
        raise PriceQueueError("Invalid plan")
    _query(plan["context"])
    steps = plan.get("steps")
    titles = [row["Title"] for row in plan["context"]["SearchWords"]["Candidates"]]
    if not isinstance(steps, list) or not 1 <= len(steps) <= 32:
        raise PriceQueueError("Expected 1 to 32 filter steps")
    seen = set()
    for step in steps:
        if (not isinstance(step, dict) or type(step.get("enabled")) is not bool
                or not isinstance(step.get("word"), str) or titles.count(step["word"]) != 1
                or step["word"] in seen):
            raise PriceQueueError("Filter steps require unique observed words and explicit states")
        seen.add(step["word"])


class FilterPlan:
    def __init__(self, directory):
        self.directory = Path(directory)

    @classmethod
    def create(cls, directory, context, process_id, steps):
        plan = {"schema": 1, "process_id": process_id, "context": context, "steps": steps}
        _validate(plan)
        result = cls(directory)
        result.directory.mkdir(parents=True, exist_ok=False)
        _save(result.directory / "plan.json", plan)
        return result

    def _state(self):
        try:
            plan = _read(self.directory / "plan.json")
            _validate(plan)
            context = plan["context"]
            completed, pending = 0, None
            events = sorted(self.directory.glob("event-*.json"))
            for index, path in enumerate(events):
                if path.name != f"event-{index:04d}.json":
                    raise PriceQueueError("Missing event; replay blocked")
                event = _read(path)
                if event["kind"] == "begin":
                    if pending is not None or completed >= len(plan["steps"]) or event["step"] != completed:
                        raise PriceQueueError("Invalid begin order")
                    if _query(context) != _query(event["context"]):
                        raise PriceQueueError("Source or filters changed")
                    pending = event
                elif event["kind"] == "observed":
                    if pending is None or event["step"] != completed:
                        raise PriceQueueError("Observation without pending step")
                    intent = {**plan["steps"][completed], "before": pending["context"]["SearchWords"],
                              "process_id": plan["process_id"], "can_write": False}
                    reconcile_search(intent, event["first"], event["second"], plan["process_id"])
                    # Preserve both panel handles and complete source identity across the step.
                    if _context(context)[:2] != _context(event["second"])[:2]:
                        raise PriceQueueError("Source or panels changed")
                    _query(event["second"])
                    context = event["second"]
                    completed += 1
                    pending = None
                else:
                    raise PriceQueueError("Unknown event")
            return plan, context, completed, pending, len(events)
        except (OSError, ValueError, KeyError, TypeError, AttributeError) as exc:
            raise PriceQueueError("Invalid or incomplete journal; no input allowed") from exc

    def status(self):
        plan, _, completed, pending, _ = self._state()
        return {"completed_steps": completed, "total_steps": len(plan["steps"]),
                "pending_step": pending["step"] if pending is not None else None,
                "pending_action": copy.deepcopy(plan["steps"][completed]) if pending is not None else None,
                "next_step": copy.deepcopy(plan["steps"][completed]) if completed < len(plan["steps"]) and pending is None else None,
                "status": "observe_only" if pending is not None else "steps_observed" if completed == len(plan["steps"]) else "ready",
                "can_write": False, "search_completion_verified": False, "automatic_retry_allowed": False}

    def begin(self, fresh, process_id, *, confirmed=False):
        if confirmed is not True:
            raise PriceQueueError("Explicit filter confirmation required")
        plan, context, completed, pending, count = self._state()
        if pending is not None or completed >= len(plan["steps"]):
            raise PriceQueueError("Pending or completed plan; no input permitted")
        if type(process_id) is not int or process_id != plan["process_id"] or _query(context) != _query(fresh):
            raise PriceQueueError("Process, source or filters changed")
        _save(self.directory / f"event-{count:04d}.json", {"kind": "begin", "step": completed, "context": fresh})
        return self.status()

    def observe(self, first, second, process_id):
        plan, context, completed, pending, count = self._state()
        if pending is None or type(process_id) is not int or process_id != plan["process_id"]:
            raise PriceQueueError("No pending step or process changed")
        intent = {**plan["steps"][completed], "before": pending["context"]["SearchWords"],
                  "process_id": process_id, "can_write": False}
        reconcile_search(intent, first, second, process_id)
        if _context(context)[:2] != _context(second)[:2]:
            raise PriceQueueError("Source or panels changed")
        _query(second)
        _save(self.directory / f"event-{count:04d}.json", {"kind": "observed", "step": completed, "first": first, "second": second})
        return self.status()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("create", "begin", "observe", "status"))
    parser.add_argument("--directory", required=True, type=Path)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--confirmed", action="store_true")
    args = parser.parse_args()
    journal = FilterPlan(args.directory)
    if args.operation != "status":
        if args.report is None: parser.error("--report required")
        report = _read(args.report)
        if args.operation == "create":
            journal = FilterPlan.create(args.directory, report["context"], report["process_id"], report["steps"])
        elif args.operation == "begin":
            journal.begin(report["context"], report["process_id"], confirmed=args.confirmed)
        else:
            journal.observe(report["first"], report["second"], report["process_id"])
    print(json.dumps(journal.status(), ensure_ascii=False))


if __name__ == "__main__": main()
