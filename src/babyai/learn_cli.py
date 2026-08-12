import typer

from .autodidact import Autodidact, AutodidactProtocolError, LessonCandidateStore
from .cli import build_provider
from .config import BabyAIConfig
from .llm import LLMError
from .memory import MemoryKind, SQLiteMemoryStore

app = typer.Typer(help="Manage BabyAI AUTODIDACT lesson candidates")


def candidate_store() -> LessonCandidateStore:
    return LessonCandidateStore(BabyAIConfig.default().lesson_candidate_file)


@app.command("propose")
def propose(context: str) -> None:
    config = BabyAIConfig.default()
    try:
        candidate = Autodidact(build_provider(config)).propose(context)
    except (LLMError, AutodidactProtocolError, ValueError) as exc:
        typer.echo(f"Could not create lesson candidate: {exc}", err=True)
        raise typer.Exit(code=7) from exc
    candidate_store().save(candidate)
    typer.echo(candidate.as_context())
    typer.echo("Pending only. Run 'babyai-learn approve' to write durable knowledge.")


@app.command("show")
def show() -> None:
    candidate = candidate_store().load()
    typer.echo(candidate.as_context() if candidate else "No pending lesson candidate.")


@app.command("approve")
def approve() -> None:
    config = BabyAIConfig.default()
    store = candidate_store()
    candidate = store.load()
    if candidate is None:
        typer.echo("No pending lesson candidate.", err=True)
        raise typer.Exit(code=7)
    record = SQLiteMemoryStore(config.memory_db).add(
        "autodidact",
        candidate.knowledge,
        kind=MemoryKind.KNOWLEDGE,
    )
    store.clear()
    typer.echo(f"learned #{record.id} [knowledge] {candidate.knowledge}")


@app.command("reject")
def reject() -> None:
    candidate_store().clear()
    typer.echo("Pending lesson candidate rejected.")


if __name__ == "__main__":
    app()
