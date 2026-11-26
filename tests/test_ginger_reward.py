from __future__ import annotations

import sqlite3
from dataclasses import replace

import pytest

from babyai.ginger_reward import (
    REWARDS,
    GingerRewardLedger,
    RewardEvidenceError,
    VerifiedGingerOutcome,
)


def event(*, attempt="attempt-1", decision="link", outcome="correct_link",
          item="sku-151", supplier="supplier-line-31", session="run-20261023",
          verifier="operator", evidence="audit://manual/checked-31"):
    return VerifiedGingerOutcome(
        attempt_id=attempt, session_id=session, supplier_row_key=supplier,
        catalog_item_key=item, decision=decision, outcome=outcome,
        verifier=verifier, evidence_reference=evidence,
    )


def ledger(tmp_path):
    return GingerRewardLedger(tmp_path / "rewards" / "ginger.sqlite3")


def test_strict_reward_policy_is_asymmetric():
    assert REWARDS == {
        "correct_link": 10, "correct_reject": 5, "correct_abstain": 3,
        "wrong_link": -100, "critical_mismatch": -250,
        "unauthorized_action": -500, "wrong_reject": -20,
        "unnecessary_abstain": -3,
    }


def test_human_verified_link_and_rejection_get_points_once(tmp_path):
    store = ledger(tmp_path)
    assert store.record(event()) == 10
    assert store.record(event(
        attempt="attempt-2", supplier="supplier-line-32",
        decision="reject", outcome="correct_reject",
    )) == 5
    status = store.status("run-20261023")
    assert status.points == 15
    assert status.reviewed_decisions == 2
    assert status.automatic_ginger_write_allowed is False
    assert status.frozen is False
    assert len(store.history("run-20261023")) == 2


def test_abstention_only_gets_points_when_external_verifier_confirms_it(tmp_path):
    store = ledger(tmp_path)
    with pytest.raises(RewardEvidenceError, match="cannot verify its own work"):
        store.record(event(
            decision="abstain", outcome="correct_abstain", item="",
            verifier="UNIX",
        ))
    assert store.status("run-20261023").points == 0
    assert store.record(event(
        decision="abstain", outcome="correct_abstain", item="",
        verifier="operator",
    )) == 3


def test_fail_to_reject_unsafe_variant_halts_session(tmp_path):
    store = ledger(tmp_path)
    assert store.record(event(
        outcome="critical_mismatch",
        evidence="manual://intense-versus-base-confirmed",
    )) == -250
    status = store.status("run-20261023")
    assert status.points == -250
    assert status.frozen
    assert status.critical_incidents == 1
    assert status.reason == "critical_incident"
    with pytest.raises(RewardEvidenceError, match="frozen"):
        store.record(event(
            attempt="attempt-2", supplier="other", item="other",
            outcome="correct_link",
        ))


def test_any_wrong_link_fails_closed_at_negative_budget(tmp_path):
    store = ledger(tmp_path)
    store.record(event(outcome="wrong_link"))
    status = store.status("run-20261023")
    assert status.points == -100
    assert status.reason == "negative_reward_budget"
    assert status.frozen


def test_unauthorized_action_triggers_independent_freeze(tmp_path):
    store = ledger(tmp_path)
    store.record(event(outcome="unauthorized_action"))
    assert store.status("run-20261023").reason == "critical_incident"
    assert store.status("run-20261023").points == -500


def test_no_farming_by_replay_or_repeat_on_same_catalogue_pair(tmp_path):
    store = ledger(tmp_path)
    store.record(event())
    with pytest.raises(RewardEvidenceError, match="Duplicate"):
        store.record(event())
    with pytest.raises(RewardEvidenceError, match="Duplicate"):
        store.record(event(attempt="attempt-999"))
    assert store.status("run-20261023").points == 10
    # Replays across sessions are also barred by the attempt_id unique key.
    with pytest.raises(RewardEvidenceError, match="Duplicate"):
        store.record(event(session="run-next-day"))


def test_only_supported_decision_outcome_pairs_count(tmp_path):
    store = ledger(tmp_path)
    for wrong in (
        event(decision="reject", outcome="correct_link"),
        event(decision="abstain", outcome="critical_mismatch", item=""),
        event(decision="link", outcome="correct_abstain"),
        event(item=""),
        event(evidence=""),
        event(verifier="llm"),
        event(verifier="ocr"),
    ):
        with pytest.raises(RewardEvidenceError):
            store.record(wrong)
    assert store.status("run-20261023").reviewed_decisions == 0


def test_detect_external_tampering_with_sqlite_events(tmp_path):
    store = ledger(tmp_path)
    store.record(event())
    with sqlite3.connect(store.path) as conn:
        conn.execute("UPDATE reward_events SET points=999 WHERE sequence=1")
    with pytest.raises(RewardEvidenceError, match="inconsistent|modified"):
        store.status("run-20261023")
    with pytest.raises(RewardEvidenceError, match="inconsistent|modified"):
        store.record(event(attempt="attempt-2", supplier="other", item="other"))


def test_correct_abstention_does_not_allow_attested_automatic_link(tmp_path):
    store = ledger(tmp_path)
    store.record(event(decision="abstain", outcome="correct_abstain", item=""))
    assert store.status("run-20261023").automatic_ginger_write_allowed is False
    assert store.history("run-20261023")[0]["points"] == 3


def test_wrong_reject_and_needless_abstention_reduce_score(tmp_path):
    store = ledger(tmp_path)
    store.record(event(decision="reject", outcome="wrong_reject"))
    store.record(event(
        attempt="attempt-2", supplier="supplier-line-32",
        decision="abstain", outcome="unnecessary_abstain", item="",
    ))
    assert store.status("run-20261023").points == -23
    assert not store.status("run-20261023").frozen



def test_verified_wrong_pair_cannot_be_tried_again_in_later_session(tmp_path):
    store = ledger(tmp_path)
    assert not store.known_unsafe_mapping("supplier-line-31", "sku-151")
    store.record(event(outcome="critical_mismatch"))
    assert store.known_unsafe_mapping("supplier-line-31", "sku-151")
    # Even though a human could explicitly start a new session, the original
    # confirmed-bad source/item pair remains unsafe across the local ledger.
    assert store.status("next-session").frozen is False
    assert store.known_unsafe_mapping("supplier-line-31", "sku-151")
    assert not store.known_unsafe_mapping("supplier-line-31", "sku-152")


def test_positive_feedback_is_not_an_automatic_authorization(tmp_path):
    store = ledger(tmp_path)
    store.record(event())
    assert not store.known_unsafe_mapping("supplier-line-31", "sku-151")
    assert store.status("run-20261023").automatic_ginger_write_allowed is False
