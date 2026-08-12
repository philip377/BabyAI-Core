from __future__ import annotations

import typer

from .config import BabyAIConfig
from .evidence import EvidenceEvaluator, EvidenceProtocolError, EvidenceStore
from .hypothesis import HypothesisStore
from .llm import LLMError
from .cli import build_provider

app = typer.Typer(help="Manage BabyAI evidence for the current hypothesis")


def evidence_store() -> EvidenceStore:
    return EvidenceStore(BabyAIConfig.default().evidence_file)


def hypothesis_store() -> HypothesisStore:
    return HypothesisStore(BabyAIConfig.default().hypothesis_file)


@app.command("add")
def add(observation: str) -> None:
    try:
        state = evidence_store().add(observation)
    except ValueError as exc:
        typer.echo(str(exc), err=True)
        raise typer.Exit(code=6) from exc
    typer.echo(f"Evidence added. total={len(state.items)}")


@app.command("show")
def show() -> None:
    state = evidence_store().load()
    if not state.items:
        typer.echo("No evidence observations.")
        return
    for index, item in enumerate(state.items, start=1):
        typer.echo(f"{index}. {item.observation}")
    if state.assessment is not None:
        typer.echo(state.assessment.as_context())


@app.command("assess")
def assess() -> None:
    config = BabyAIConfig.default()
    hypothesis = hypothesis_store().load()
    if hypothesis is None:
        typer.echo("No stored hypothesis.", err=True)
        raise typer.Exit(code=6)
    state = evidence_store().load()
    if not state.items:
        typer.echo("No evidence observations.", err=True)
        raise typer.Exit(code=6)
    try:
        assessment = EvidenceEvaluator(build_provider(config)).assess(hypothesis, state.items)
    except (LLMError, EvidenceProtocolError, ValueError) as exc:
        typer.echo(f"Could not assess evidence: {exc}", err=True)
        raise typer.Exit(code=6) from exc
    evidence_store().set_assessment(assessment)
    typer.echo(assessment.as_context())
    typer.echo("Hypothesis status was not changed.")


@app.command("clear")
def clear() -> None:
    evidence_store().clear()
    typer.echo("Evidence cleared.")


if __name__ == "__main__":
    app()
