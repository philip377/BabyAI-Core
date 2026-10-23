"""Strict, human-verified reward ledger for UNIX/Ginger decision quality.

No reinforcement-model update, no ERP calls, no GUI automation and no
authority to link products. Never accept LLM/OCR self-assessed success.
This is an offline scoring/auditing component: safety permissions are separate.
"""
from __future__ import annotations

import hashlib
import json
import sqlite3
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Literal


Decision = Literal["link", "reject", "abstain"]
Outcome = Literal[
    "correct_link", "correct_reject", "correct_abstain",
    "wrong_link", "critical_mismatch", "unauthorized_action",
    "wrong_reject", "unnecessary_abstain",
]
REWARDS: dict[Outcome, int] = {
    "correct_link": 10,
    "correct_reject": 5,
    "correct_abstain": 3,
    "wrong_link": -100,
    "critical_mismatch": -250,
    "unauthorized_action": -500,
    "wrong_reject": -20,
    "unnecessary_abstain": -3,
}

# A refusal can be scored as correct only after external confirmation.
_ALLOWED: dict[str, frozenset[str]] = {
    "link": frozenset({"correct_link", "wrong_link", "critical_mismatch", "unauthorized_action"}),
    "reject": frozenset({"correct_reject", "wrong_reject", "unauthorized_action"}),
    "abstain": frozenset({"correct_abstain", "unnecessary_abstain", "unauthorized_action"}),
}
_CRITICAL = frozenset({"critical_mismatch", "unauthorized_action"})
_GENESIS = "0" * 64


class RewardEvidenceError(ValueError):
    """A proposed reward lacks independent evidence or violates the contract."""


@dataclass(frozen=True, slots=True)
class VerifiedGingerOutcome:
    attempt_id: str
    session_id: str
    supplier_row_key: str
    catalog_item_key: str
    decision: Decision
    outcome: Outcome
    verifier: str
    evidence_reference: str


@dataclass(frozen=True, slots=True)
class RewardStatus:
    session_id: str
    points: int
    reviewed_decisions: int
    wrong_links: int
    critical_incidents: int
    frozen: bool
    reason: str
    automatic_ginger_write_allowed: bool = False

    def as_dict(self) -> dict[str, object]:
        return asdict(self)


def _field(value: str, field: str, *, required: bool = True) -> str:
    if not isinstance(value, str):
        raise RewardEvidenceError(field + " must be a string")
    clean = value.strip()
    if (required and not clean) or len(clean) > 320 or any(
        ord(char) < 32 for char in clean
    ):
        raise RewardEvidenceError(field + " is invalid")
    return clean


def validate_outcome(record: VerifiedGingerOutcome) -> VerifiedGingerOutcome:
    """The judge must be outside the model: CLI confirmation or trusted audit."""
    for key in ("attempt_id", "session_id", "supplier_row_key",
                "verifier", "evidence_reference"):
        _field(getattr(record, key), key)
    _field(record.catalog_item_key, "catalog_item_key", required=False)
    if record.decision not in _ALLOWED:
        raise RewardEvidenceError("Unknown decision")
    if record.outcome not in _ALLOWED[record.decision]:
        raise RewardEvidenceError("Outcome inconsistent with the action")
    if record.decision == "link" and not record.catalog_item_key.strip():
        raise RewardEvidenceError("A link requires a real catalogue item identifier")
    if record.decision == "reject" and not record.catalog_item_key.strip():
        raise RewardEvidenceError("A rejection requires a specific catalogue candidate")
    if record.decision == "abstain" and record.catalog_item_key.strip():
        raise RewardEvidenceError("Abstention has no selected catalogue item")
    # Deliberately reject data sent directly by the agent as proof of success.
    if record.verifier.casefold().strip() in {
        "unix", "babyai", "llm", "ocr", "self", "model", "agent",
    }:
        raise RewardEvidenceError("An AI/OCR cannot verify its own work")
    return record


def _hash_event(entry: dict[str, object]) -> str:
    canonical = json.dumps(entry, sort_keys=True, ensure_ascii=False, separators=(",", ":"))
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


class GingerRewardLedger:
    """SQLite transactional, evidence-gated scoring. NEVER an action executor."""

    def __init__(self, path: str | Path):
        self.path = Path(path)

    def _connect(self) -> sqlite3.Connection:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        conn = sqlite3.connect(self.path, timeout=10)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA busy_timeout=10000")
        conn.execute("""
          CREATE TABLE IF NOT EXISTS reward_events (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            attempt_id TEXT NOT NULL UNIQUE,
            session_id TEXT NOT NULL,
            supplier_row_key TEXT NOT NULL,
            catalog_item_key TEXT NOT NULL,
            decision TEXT NOT NULL,
            outcome TEXT NOT NULL,
            verifier TEXT NOT NULL,
            evidence_reference TEXT NOT NULL,
            points INTEGER NOT NULL,
            verified_utc TEXT NOT NULL,
            previous_hash TEXT NOT NULL,
            event_hash TEXT NOT NULL
          )
        """)
        conn.execute("""
          CREATE UNIQUE INDEX IF NOT EXISTS reward_no_farm
          ON reward_events(session_id, supplier_row_key, catalog_item_key, decision)
        """)
        conn.commit()
        return conn

    @staticmethod
    def _verify_chain(conn: sqlite3.Connection) -> tuple[str, int]:
        previous = _GENESIS
        last_seq = 0
        rows = conn.execute("SELECT * FROM reward_events ORDER BY sequence ASC")
        for row in rows:
            data = dict(row)
            digest = data.pop("event_hash")
            if (data["previous_hash"] != previous or data["sequence"] != last_seq + 1:
                raise RewardEvidenceError("Reward audit chain is damaged")
            if data["outcome"] not in REWARDS or data["points"] != REWARDS[data["outcome"]]:
                raise RewardEvidenceError("Reward value is inconsistent with policy")
            if digest != _hash_event(data):
                raise RewardEvidenceError("Reward audit evidence was modified")
            previous, last_seq = digest, data["sequence"]
        return previous, last_seq

    def record(self, event: VerifiedGingerOutcome) -> int:
        """Append one externally verified judgment; repeat/replay is refused."""
        validate_outcome(event)
        with self._connect() as conn:
            conn.execute("BEGIN IMMEDIATE")
            previous, last_seq = self._verify_chain(conn)
            # Session shutdown is irreversible within this session. New sessions
            # are a deliberate operator responsibility, NEVER auto-created here.
            status = self._status(conn, event.session_id)
            if status.frozen:
                raise RewardEvidenceError("Session is frozen: operator review required")
            record = {
                "sequence": last_seq + 1,
                "attempt_id": event.attempt_id.strip(),
                "session_id": event.session_id.strip(),
                "supplier_row_key": event.supplier_row_key.strip(),
                "catalog_item_key": event.catalog_item_key.strip(),
                "decision": event.decision,
                "outcome": event.outcome,
                "verifier": event.verifier.strip(),
                "evidence_reference": event.evidence_reference.strip(),
                "points": REWARDS[event.outcome],
                "verified_utc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
                "previous_hash": previous,
            }
            digest = _hash_event(record)
            try:
                conn.execute("""
                    INSERT INTO reward_events (
                      sequence, attempt_id, session_id, supplier_row_key,
                      catalog_item_key, decision, outcome, verifier,
                      evidence_reference, points, verified_utc, previous_hash, event_hash
                    ) VALUES (
                      :sequence, :attempt_id, :session_id, :supplier_row_key,
                      :catalog_item_key, :decision, :outcome, :verifier,
                      :evidence_reference, :points, :verified_utc, :previous_hash, :event_hash
                    )
                """, {**record, "event_hash": digest})
            except sqlite3.IntegrityError as exc:
                raise RewardEvidenceError("Duplicate attempt or repeated decision: no reward") from exc
        return REWARDS[event.outcome]

    @staticmethod
    def _status(conn: sqlite3.Connection, session: str) -> RewardStatus:
        events = list(conn.execute(
            "SELECT outcome, points FROM reward_events WHERE session_id=? ORDER BY sequence",
            (session,),
        ))
        total = sum(row["points"] for row in events)
        wrong = sum(row["outcome"] == "wrong_link" for row in events)
        critical = sum(row["outcome"] in _CRITICAL for row in events)
        reason = "ready_for_review"
        if critical:
            reason = "critical_incident"
        elif wrong >= 2:
            reason = "repeated_wrong_links"
        elif total <= -75:
            reason = "negative_reward_budget"
        return RewardStatus(
            session_id=session, points=total, reviewed_decisions=len(events),
            wrong_links=wrong, critical_incidents=critical,
            frozen=reason != "ready_for_review", reason=reason,
        )

    def status(self, session_id: str) -> RewardStatus:
        session = _field(session_id, "session_id")
        with self._connect() as conn:
            self._verify_chain(conn)
            return self._status(conn, session)

    def history(self, session_id: str) -> list[dict[str, object]]:
        session = _field(session_id, "session_id")
        with self._connect() as conn:
            self._verify_chain(conn)
            return [
                dict(row) for row in conn.execute(
                    "SELECT * FROM reward_events WHERE session_id=? ORDER BY sequence",
                    (session,),
                )
            ]
