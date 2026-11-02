"""Bounded review-only row pass using guarded controllers. No implicit retries."""
import argparse
from contextlib import contextmanager
import json
import os
from pathlib import Path
import subprocess
from .ginger_filter_plan import FilterPlan, _save
from .ginger_row_journal import RowNavigationJournal
from .ginger_review_queue import ReviewQueue
from .ginger_price_queue import PriceQueueError


@contextmanager
def batch_lock(row_directory):
    path = Path(str(Path(row_directory).resolve()) + ".batch.lock")
    with path.open("a+b") as stream:
        if stream.tell() == 0:
            stream.write(b"0"); stream.flush()
        stream.seek(0)
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        try: yield
        finally:
            stream.seek(0)
            if os.name == "nt": msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else: fcntl.flock(stream.fileno(), fcntl.LOCK_UN)


def reserve_batch(row_directory, run_directory):
    history = Path(str(Path(row_directory).resolve()) + ".batches")
    history.mkdir(parents=True, exist_ok=True)
    intents = sorted(history.glob("*-intent.json"))
    for index, path in enumerate(intents):
        if path.name != f"{index:04d}-intent.json" or not (history/f"{index:04d}-complete.json").is_file():
            raise PriceQueueError("Earlier batch unresolved; reconcile it before a new run")
        # Incomplete JSON also blocks a new run.
        from .ginger_filter_plan import _read
        _read(path)
        if _read(history/f"{index:04d}-complete.json").get("status") != "bounded_pass_complete":
            raise PriceQueueError("Earlier batch completion is invalid")
    index = len(intents)
    _save(history/f"{index:04d}-intent.json", {"run_directory": str(Path(run_directory).resolve())})
    return history/f"{index:04d}-complete.json"


class ControllerBackend:
    def __init__(self, row_directory, runner):
        self.row_directory = Path(row_directory)
        self.runner = Path(runner)

    def status(self): return RowNavigationJournal(self.row_directory).status()
    def queue_status(self): return ReviewQueue(self.row_directory).status()
    def filter_status(self, directory): return FilterPlan(directory).status()

    def call(self, operation, target):
        if operation == "FilterStep":
            args = ["--filter-controller", "Step", str(target), "--confirm-navigation"]
        elif operation == "FinishReview":
            args = ["--finish-row-review", str(self.row_directory), str(target)]
        elif operation in ("NextReviewed", "PrepareFilters"):
            args = ["--row-controller", operation, str(self.row_directory)]
            if operation == "NextReviewed": args.append("--confirm-navigation")
        else: raise PriceQueueError("Unsupported batch action")
        # Existing fixed runner bounds output, subprocess lifetime and kills its child tree.
        process = subprocess.Popen(["dotnet", str(self.runner), *args], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            stdout, stderr = process.communicate(timeout=150)
        except subprocess.TimeoutExpired:
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, timeout=15)
            else: process.kill()
            process.communicate()
            raise PriceQueueError("Controller timeout; inspect journals, do not retry")
        if process.returncode != 0:
            raise PriceQueueError("Controller failed; inspect its saved diagnostics, do not retry")
        if len(stdout) > 4_000_000: raise PriceQueueError("Controller output too large")
        return json.loads(stdout.decode("utf-8-sig"))


def run_batch(backend, run_directory, max_rows, *, confirmed=False):
    if confirmed is not True or type(max_rows) is not int or not 1 <= max_rows <= 3:
        raise PriceQueueError("Explicit confirmation and 1 to 3 rows required")
    directory = Path(run_directory)
    directory.mkdir(parents=True, exist_ok=False)
    _save(directory/"plan.json", {"max_new_reviews": max_rows, "can_write": False})
    events, completed = 0, []
    def act(operation, target=None):
        nonlocal events
        index = events; events += 1
        _save(directory/f"action-{index:03d}-intent.json", {"operation": operation, "target": str(target) if target else None})
        result = backend.call(operation, target)
        _save(directory/f"action-{index:03d}-observed.json", result)
        return result
    try:
        for _ in range(max_rows):
            state = backend.status()
            if state["pending_attempt"] is not None:
                raise PriceQueueError("Pending row navigation; observation required")
            if state["selected_sampled"]:
                if not backend.queue_status()["current_review_queued"]:
                    raise PriceQueueError("Current review is not queued")
                if state["next_position"] is None: break
                previous = state["selected_position"]
                act("NextReviewed")
                state = backend.status()
                if state["selected_position"] != previous+1 or state["pending_attempt"] is not None or state["selected_sampled"]:
                    raise PriceQueueError("Next row not verified")
            position = state["selected_position"]
            prepared = act("PrepareFilters")
            preparation = prepared["diagnostics"]
            plan = prepared["status"]["plan_directory"]
            if plan is not None:
                for _ in range(32):
                    filters = backend.filter_status(plan)
                    if filters["pending_step"] is not None:
                        raise PriceQueueError("Pending filter; observation required")
                    if filters["next_step"] is None: break
                    act("FilterStep", plan)
                filters = backend.filter_status(plan)
                if filters["pending_step"] is not None or filters["status"] != "steps_observed":
                    raise PriceQueueError("Filter plan not complete")
            act("FinishReview", preparation)
            state = backend.status()
            queued = backend.queue_status()
            if (state["selected_position"] != position or not state["selected_sampled"]
                    or state["pending_attempt"] is not None or not queued["current_review_queued"]):
                raise PriceQueueError("Review was not recorded and queued")
            completed.append(position)
            _save(directory/f"row-{position:04d}.json", {"position": position, "status": "needs_manual_review", "can_write": False})
        result = {"reviewed_positions": completed, "status": "bounded_pass_complete",
                  "can_write": False, "products_processed": False, "human_approved": False}
        _save(directory/"result.json", result)
        return result
    except Exception as exc:
        _save(directory/"stopped.json", {"reviewed_positions": completed, "error": str(exc), "automatic_retry_allowed": False})
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--row-directory", type=Path, required=True)
    parser.add_argument("--run-directory", type=Path, required=True)
    parser.add_argument("--max-rows", type=int, required=True)
    parser.add_argument("--confirmed", action="store_true")
    args = parser.parse_args()
    if not args.confirmed: parser.error("--confirmed required")
    runner = Path(__file__).resolve().parents[2]/"tests/GingerDiagnostics.Smoke/bin/Release/net10.0/GingerDiagnostics.Smoke.dll"
    if not runner.is_file(): raise PriceQueueError("Build the fixed diagnostics runner first")
    with batch_lock(args.row_directory):
        if type(args.max_rows) is not int or not 1 <= args.max_rows <= 3 or args.run_directory.exists():
            raise PriceQueueError("New run directory and 1 to 3 rows required")
        completion = reserve_batch(args.row_directory, args.run_directory)
        result = run_batch(ControllerBackend(args.row_directory, runner), args.run_directory, args.max_rows, confirmed=True)
        _save(completion, result)
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__": main()
