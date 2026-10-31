from dataclasses import replace
from concurrent.futures import ThreadPoolExecutor
import json

import pytest

from babyai.ginger_guarded_driver import GuardedGingerDriver, GingerGuardError, GingerOperationJournal, Selection


BASE = Selection(101, 202, "document-1", "supplier-1", "Intense 30ml",
                 "sku-9", "Intense 30ml", "", True)


class Adapter:
    def __init__(self):
        self.state = BASE
        self.calls = 0
        self.fail = False

    def read_selection(self):
        return self.state

    def link_if_unchanged(self, expected):
        assert self.state == expected
        self.calls += 1
        if self.fail:
            raise OSError("unknown outcome")
        self.state = replace(self.state, linked_product=self.state.candidate_row)


def make_driver(tmp_path, adapter, confirm=lambda selection: True, authorize=lambda: None,
                journal=lambda message: None):
    return GuardedGingerDriver(adapter, confirm=confirm, authorize=authorize, journal=journal,
                              operations=GingerOperationJournal(tmp_path / "operations"))


def test_verified_link_and_duplicate_refused(tmp_path):
    adapter = Adapter()
    log = []
    driver = make_driver(tmp_path, adapter, journal=log.append)
    assert driver.link().linked_product == "sku-9"
    with pytest.raises(GingerGuardError):
        driver.link()
    assert adapter.calls == 1
    assert "подтверждено" in log[-1]


@pytest.mark.parametrize("field,value", [
    ("window", 999), ("process", 999), ("document", "other"),
    ("supplier_row", "other"), ("supplier_title", "other"),
    ("candidate_row", "other"), ("candidate_title", "other"),
    ("linked_product", "other"), ("verified", False),
])
def test_selection_change_during_confirmation_prevents_dispatch(tmp_path, field, value):
    adapter = Adapter()
    def confirm(selection):
        adapter.state = replace(selection, **{field: value})
        return True
    with pytest.raises(GingerGuardError):
        make_driver(tmp_path, adapter, confirm).link()
    assert adapter.calls == 0


@pytest.mark.parametrize("answer", [False, None, "yes", 1])
def test_confirmation_must_be_explicit_boolean(tmp_path, answer):
    adapter = Adapter()
    with pytest.raises(GingerGuardError):
        make_driver(tmp_path, adapter, lambda selection: answer).link()
    assert adapter.calls == 0


def test_unknown_outcome_blocks_retry(tmp_path):
    adapter = Adapter()
    adapter.fail = True
    driver = make_driver(tmp_path, adapter)
    with pytest.raises(OSError):
        driver.link()
    with pytest.raises(GingerGuardError):
        driver.link()
    assert adapter.calls == 1


def test_wrong_result_blocks_retry(tmp_path):
    adapter = Adapter()
    def wrong(expected):
        adapter.calls += 1
        adapter.state = replace(BASE, linked_product="wrong-sku")
    adapter.link_if_unchanged = wrong
    driver = make_driver(tmp_path, adapter)
    with pytest.raises(GingerGuardError):
        driver.link()
    assert driver.blocked


def test_permission_revoked_while_confirming(tmp_path):
    adapter = Adapter()
    allowed = True
    def authorize():
        if not allowed:
            raise PermissionError()
    def confirm(selection):
        nonlocal allowed
        allowed = False
        return True
    with pytest.raises(PermissionError):
        make_driver(tmp_path, adapter, confirm, authorize).link()
    assert adapter.calls == 0


def test_journal_failure_prevents_dispatch(tmp_path):
    adapter = Adapter()
    def journal(message):
        raise OSError("disk full")
    with pytest.raises(OSError):
        make_driver(tmp_path, adapter, journal=journal).link()
    assert adapter.calls == 0


def test_reentrant_confirmation_cannot_dispatch_second_action(tmp_path):
    adapter = Adapter()
    def confirm(selection):
        with pytest.raises(GingerGuardError):
            driver.link()
        return True
    driver = make_driver(tmp_path, adapter, confirm)
    driver.link()
    assert adapter.calls == 1


def test_restart_keeps_unknown_outcome_blocked(tmp_path):
    first = Adapter()
    first.fail = True
    with pytest.raises(OSError):
        make_driver(tmp_path, first).link()
    restarted = Adapter()
    with pytest.raises(GingerGuardError):
        make_driver(tmp_path, restarted).link()
    assert restarted.calls == 0


def test_truncated_pending_record_fails_closed(tmp_path):
    operations = GingerOperationJournal(tmp_path / "operations")
    operations.pending.write_text('{"version":', encoding="utf-8")
    adapter = Adapter()
    with pytest.raises(GingerGuardError):
        make_driver(tmp_path, adapter).link()
    assert adapter.calls == 0


def test_exclusive_intent_across_journal_instances(tmp_path):
    def reserve(_):
        try:
            return GingerOperationJournal(tmp_path).begin(BASE)
        except GingerGuardError:
            return None
    with ThreadPoolExecutor(max_workers=8) as pool:
        results = list(pool.map(reserve, range(8)))
    assert sum(item is not None for item in results) == 1


def test_intent_is_saved_before_dispatch_and_verified_record_retained(tmp_path):
    adapter = Adapter()
    original = adapter.link_if_unchanged
    def inspect_intent(expected):
        record = json.loads((tmp_path / "operations" / "pending.json").read_text(encoding="utf-8"))
        assert record["selection"]["supplier_row"] == expected.supplier_row
        assert record["state"] == "pending"
        original(expected)
    adapter.link_if_unchanged = inspect_intent
    make_driver(tmp_path, adapter).link()
    assert not (tmp_path / "operations" / "pending.json").exists()
    archived, = (tmp_path / "operations").glob("verified-*.json")
    record = json.loads(archived.read_text(encoding="utf-8"))
    assert record["state"] == "verified"
    assert record["result"]["linked_product"] == "sku-9"


def test_failed_intent_flush_prevents_dispatch(tmp_path, monkeypatch):
    def fail(_):
        raise OSError("disk error")
    monkeypatch.setattr("babyai.ginger_guarded_driver.os.fsync", fail)
    adapter = Adapter()
    with pytest.raises(OSError):
        make_driver(tmp_path, adapter).link()
    assert adapter.calls == 0
    with pytest.raises(GingerGuardError):
        make_driver(tmp_path, adapter).link()


def test_failed_completion_flush_blocks_after_restart(tmp_path, monkeypatch):
    import os
    original = os.fsync
    calls = 0
    def fail_second(fd):
        nonlocal calls
        calls += 1
        if calls == 2:
            raise OSError("disk full")
        original(fd)
    monkeypatch.setattr("babyai.ginger_guarded_driver.os.fsync", fail_second)
    adapter = Adapter()
    with pytest.raises(OSError):
        make_driver(tmp_path, adapter).link()
    assert adapter.calls == 1
    with pytest.raises(GingerGuardError):
        make_driver(tmp_path, Adapter()).link()


def test_wrong_completion_cannot_clear_pending(tmp_path):
    journal = GingerOperationJournal(tmp_path)
    operation = journal.begin(BASE)
    with pytest.raises(GingerGuardError):
        journal.complete(operation, replace(BASE, linked_product="wrong"))
    assert journal.has_pending()
