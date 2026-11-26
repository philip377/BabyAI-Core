"""Operator-only CLI for the local, externally verified UNIX/Ginger reward ledger.

This CLI never manipulates Ginger. It is intentionally NOT a model tool.
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Optional

import typer

from .ginger_ocr_match_review import review_ginger_cpu_ocr_json
from .ginger_reward import (
    GingerRewardLedger,
    RewardEvidenceError,
    VerifiedGingerOutcome,
)

app = typer.Typer(help="Ginger reward ledger: verified outcomes only; never ERP actions")


def _default_db() -> Path:
    import os
    if os.name == "nt":
        return Path(os.environ.get("LOCALAPPDATA", str(Path.home()))) / "BabyAI" / "ginger-rewards.sqlite3"
    return Path.home() / ".babyai" / "ginger-rewards.sqlite3"


@app.command("preview")
def preview(
    report: Path = typer.Argument(..., exists=True, readable=True, help="Previous ginger-cpu-report.json"),
) -> None:
    """Review OCR hypotheses. Score stays zero until independent verification."""
    verdict = review_ginger_cpu_ocr_json(report)
    verdict["points_awarded"] = 0
    verdict["reward_verification"] = "unverified"
    typer.echo(json.dumps(verdict, ensure_ascii=False, indent=2))


@app.command("record")
def record(
    attempt_id: str = typer.Option(..., help="Unique decision attempt identifier"),
    session_id: str = typer.Option(..., help="Explicit test session identifier"),
    supplier_row_key: str = typer.Option(..., help="Verified supplier row/line identity"),
    decision: str = typer.Option(..., help="link / reject / abstain"),
    outcome: str = typer.Option(..., help="Independently verified outcome"),
    verifier: str = typer.Option(..., help="Human operator or independent audit verifier"),
    evidence_reference: str = typer.Option(..., help="External proof, screenshot or audit reference"),
    catalog_item_key: str = typer.Option("", help="Verified catalogue identifier, blank on abstain"),
    db: Optional[Path] = typer.Option(None, help="Local-only reward database"),
) -> None:
    """Record a human-verified result. Never assume OCR/model success."""
    target = db if db is not None else _default_db()
    try:
        event = VerifiedGingerOutcome(
            attempt_id=attempt_id, session_id=session_id,
            supplier_row_key=supplier_row_key, catalog_item_key=catalog_item_key,
            decision=decision, outcome=outcome,
            verifier=verifier, evidence_reference=evidence_reference,
        )
        engine = GingerRewardLedger(target)
        earned = engine.record(event)
        result = {"awarded": earned, "status": engine.status(session_id).as_dict()}
    except RewardEvidenceError as exc:
        typer.echo("Denied: " + str(exc), err=True)
        raise typer.Exit(1) from exc
    typer.echo(json.dumps(result, ensure_ascii=False, indent=2))


@app.command("status")
def status(
    session_id: str = typer.Option(..., help="Existing test session identifier"),
    db: Optional[Path] = typer.Option(None, help="Local-only reward database"),
) -> None:
    """Read session score and safety freezes. Never grants Ginger privileges."""
    target = db if db is not None else _default_db()
    try:
        score = GingerRewardLedger(target).status(session_id)
    except RewardEvidenceError as exc:
        typer.echo("Denied: " + str(exc), err=True)
        raise typer.Exit(1) from exc
    typer.echo(json.dumps(score.as_dict(), ensure_ascii=False, indent=2))


if __name__ == "__main__":
    app()
