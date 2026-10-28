from dataclasses import replace

import pytest

from babyai.ginger_guarded_driver import GuardedGingerDriver, GingerGuardError, Selection


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


def make_driver(adapter, confirm=lambda selection: True, authorize=lambda: None,
                journal=lambda message: None):
    return GuardedGingerDriver(adapter, confirm=confirm, authorize=authorize, journal=journal)


def test_verified_link_and_duplicate_refused():
    adapter = Adapter()
    log = []
    driver = make_driver(adapter, journal=log.append)
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
def test_selection_change_during_confirmation_prevents_dispatch(field, value):
    adapter = Adapter()
    def confirm(selection):
        adapter.state = replace(selection, **{field: value})
        return True
    with pytest.raises(GingerGuardError):
        make_driver(adapter, confirm).link()
    assert adapter.calls == 0


@pytest.mark.parametrize("answer", [False, None, "yes", 1])
def test_confirmation_must_be_explicit_boolean(answer):
    adapter = Adapter()
    with pytest.raises(GingerGuardError):
        make_driver(adapter, lambda selection: answer).link()
    assert adapter.calls == 0


def test_unknown_outcome_blocks_retry():
    adapter = Adapter()
    adapter.fail = True
    driver = make_driver(adapter)
    with pytest.raises(OSError):
        driver.link()
    with pytest.raises(GingerGuardError):
        driver.link()
    assert adapter.calls == 1


def test_wrong_result_blocks_retry():
    adapter = Adapter()
    def wrong(expected):
        adapter.calls += 1
        adapter.state = replace(BASE, linked_product="wrong-sku")
    adapter.link_if_unchanged = wrong
    driver = make_driver(adapter)
    with pytest.raises(GingerGuardError):
        driver.link()
    assert driver.blocked


def test_permission_revoked_while_confirming():
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
        make_driver(adapter, confirm, authorize).link()
    assert adapter.calls == 0


def test_journal_failure_prevents_dispatch():
    adapter = Adapter()
    def journal(message):
        raise OSError("disk full")
    with pytest.raises(OSError):
        make_driver(adapter, journal=journal).link()
    assert adapter.calls == 0


def test_reentrant_confirmation_cannot_dispatch_second_action():
    adapter = Adapter()
    def confirm(selection):
        with pytest.raises(GingerGuardError):
            driver.link()
        return True
    driver = make_driver(adapter, confirm)
    driver.link()
    assert adapter.calls == 1
