import pytest
from babyai.ginger_batch import run_batch, batch_lock, reserve_batch
from babyai.ginger_filter_plan import _save
from babyai.ginger_price_queue import PriceQueueError


class Backend:
    def __init__(self):
        self.position=1; self.sampled=True; self.queued=True; self.pending=None
        self.calls=[]; self.filters=0; self.fail=None
    def status(self):
        return {"selected_position":self.position,"selected_sampled":self.sampled,"pending_attempt":self.pending,
                "next_position":self.position+1 if self.position<4 else None}
    def queue_status(self): return {"current_review_queued":self.queued}
    def filter_status(self, path):
        return {"pending_step":None,"next_step":{"word":"A"} if self.filters<1 else None,
                "status":"ready" if self.filters<1 else "steps_observed"}
    def call(self, operation, target):
        self.calls.append(operation)
        if self.fail==operation: raise RuntimeError("injected failure")
        if operation=="NextReviewed": self.position+=1; self.sampled=False; self.queued=False
        elif operation=="PrepareFilters":
            self.filters=0
            return {"diagnostics":"fixture","status":{"plan_directory":"filters"}}
        elif operation=="FilterStep": self.filters+=1
        elif operation=="FinishReview": self.sampled=True; self.queued=True
        return {"can_write":False}


def test_two_rows_bounded_and_reviewed(tmp_path):
    backend=Backend()
    result=run_batch(backend,tmp_path/"run",2,confirmed=True)
    assert result["reviewed_positions"]==[2,3]
    assert backend.calls==["NextReviewed","PrepareFilters","FilterStep","FinishReview"]*2
    assert not result["human_approved"] and not result["products_processed"]


@pytest.mark.parametrize("operation",["NextReviewed","PrepareFilters","FilterStep","FinishReview"])
def test_failure_stops_without_retry(tmp_path,operation):
    backend=Backend(); backend.fail=operation
    with pytest.raises(RuntimeError): run_batch(backend,tmp_path/"run",3,confirmed=True)
    assert backend.calls.count(operation)==1 and backend.calls[-1]==operation
    assert (tmp_path/"run/stopped.json").exists()
    with pytest.raises(FileExistsError): run_batch(backend,tmp_path/"run",3,confirmed=True)


def test_pending_navigation_never_sends_input(tmp_path):
    backend=Backend(); backend.pending="unresolved"
    with pytest.raises(PriceQueueError): run_batch(backend,tmp_path/"run",2,confirmed=True)
    assert backend.calls==[]


def test_unqueued_current_row_blocks_pass(tmp_path):
    backend=Backend(); backend.queued=False
    with pytest.raises(PriceQueueError): run_batch(backend,tmp_path/"run",2,confirmed=True)
    assert backend.calls==[]


@pytest.mark.parametrize("count,confirmed",[(0,True),(4,True),(True,True),(1,False)])
def test_explicit_limits_required(tmp_path,count,confirmed):
    backend=Backend()
    with pytest.raises(PriceQueueError): run_batch(backend,tmp_path/"run",count,confirmed=confirmed)
    assert not (tmp_path/"run").exists()


def test_last_row_does_not_wrap(tmp_path):
    backend=Backend(); backend.position=4
    result=run_batch(backend,tmp_path/"run",3,confirmed=True)
    assert result["reviewed_positions"]==[] and backend.calls==[]


def test_unresolved_prior_batch_blocks_new_directory(tmp_path):
    row=tmp_path/"rows"; row.mkdir()
    complete=reserve_batch(row,tmp_path/"first")
    with pytest.raises(PriceQueueError): reserve_batch(row,tmp_path/"second")
    _save(complete,{"status":"bounded_pass_complete"})
    assert reserve_batch(row,tmp_path/"second").name=="0001-complete.json"


def test_batch_lock_excludes_second_owner(tmp_path):
    row=tmp_path/"rows"; row.mkdir()
    with batch_lock(row):
        with pytest.raises(OSError):
            with batch_lock(row): pass


from test_ginger_review_queue import reviewed
from babyai.ginger_batch import reconcile_stopped_prepare


def stopped_prepare(tmp_path):
    journal, report, saved, queue = reviewed(tmp_path)
    queue.add(saved)
    run = tmp_path/"stopped"; run.mkdir()
    completion = reserve_batch(journal.directory, run)
    _save(run/"stopped.json", {"automatic_retry_allowed": False})
    _save(run/"action-000-intent.json", {"operation":"NextReviewed"})
    _save(run/"action-000-observed.json", {"status":journal.status()})
    _save(run/"action-001-intent.json", {"operation":"PrepareFilters"})
    return journal, report["rows_report"], run, completion


def test_reconcile_reviewed_prepare_unlocks_new_run_without_replay(tmp_path):
    journal, report, run, completion = stopped_prepare(tmp_path)
    result = reconcile_stopped_prepare(journal.directory, run, report)
    assert result["status"] == "stopped_reconciled"
    assert not result["original_batch_completed"] and not result["input_replayed"]
    assert reconcile_stopped_prepare(journal.directory, run, report) == result
    assert completion.exists() and (run/"stopped.json").exists()
    assert reserve_batch(journal.directory, tmp_path/"next").name == "0001-complete.json"


@pytest.mark.parametrize("defect", ["input_action", "missing_observation", "pending", "price", "process", "unqueue"])
def test_reconciliation_rejects_uncertain_state(tmp_path, defect):
    import json
    journal, report, run, completion = stopped_prepare(tmp_path)
    if defect == "input_action":
        (run/"action-001-intent.json").write_text(json.dumps({"operation":"FilterStep"}))
    elif defect == "missing_observation": (run/"action-000-observed.json").unlink()
    elif defect == "pending": journal.begin(report)
    elif defect == "price": report["rows"][0]["Observation"]["PriceText"]="999"
    elif defect == "process": report["process_id"]=999
    else:
        from babyai.ginger_review_queue import ReviewQueue
        (ReviewQueue(journal.directory).directory/"row-0001.json").unlink()
    with pytest.raises((PriceQueueError, OSError)):
        reconcile_stopped_prepare(journal.directory, run, report)
    assert not completion.exists()
    assert not (run/"reconciliation.json").exists()


def test_success_records_observed_action_timings(tmp_path):
    import json
    result=run_batch(Backend(),tmp_path/"run",1,confirmed=True)
    assert result["timing"]["completed_actions"] == 4
    assert result["timing"]["elapsed_ms"] >= result["timing"]["action_elapsed_ms"] >= 0
    observed=json.loads((tmp_path/"run/action-000-observed.json").read_text())
    assert observed["execution"]["operation"] == "NextReviewed"
    assert observed["execution"]["started_at_utc"].endswith("+00:00")


def test_controller_failure_receipt_is_bounded_and_not_retried(tmp_path):
    import json
    from babyai.ginger_batch import ControllerFailure
    class Failing(Backend):
        def call(self,operation,target):
            self.calls.append(operation)
            raise ControllerFailure("failure",9,b"x"*20000,b"diagnostic")
    backend=Failing()
    with pytest.raises(ControllerFailure): run_batch(backend,tmp_path/"run",2,confirmed=True)
    assert backend.calls == ["NextReviewed"]
    failure=json.loads((tmp_path/"run/action-000-failure.json").read_text())
    assert len(failure["controller_diagnostics"]["stdout_tail"]) == 16000
    assert failure["controller_diagnostics"]["stderr_tail"] == "diagnostic"
    assert failure["controller_diagnostics"]["output_truncated"]
    assert not failure["automatic_retry_allowed"]
    assert not (tmp_path/"run/action-000-observed.json").exists()


def test_non_object_controller_result_is_not_recorded_as_observed(tmp_path):
    class Invalid(Backend):
        def call(self,operation,target): return []
    with pytest.raises(PriceQueueError): run_batch(Invalid(),tmp_path/"run",1,confirmed=True)
    assert (tmp_path/"run/action-000-failure.json").exists()
    assert not (tmp_path/"run/action-000-observed.json").exists()
