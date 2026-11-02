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
