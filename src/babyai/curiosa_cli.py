import typer

from .cli import build_provider
from .config import BabyAIConfig
from .curiosa import Curiosa, CuriosaProtocolError, CuriosityStore
from .llm import LLMError

app = typer.Typer(help="Manage BabyAI CURIOSA uncertainty questions")


def curiosity_store() -> CuriosityStore:
    return CuriosityStore(BabyAIConfig.default().curiosity_file)


@app.command("propose")
def propose(context: str) -> None:
    config = BabyAIConfig.default()
    try:
        item = Curiosa(build_provider(config)).propose(context)
    except (LLMError, CuriosaProtocolError, ValueError) as exc:
        typer.echo(f"Could not create curiosity question: {exc}", err=True)
        raise typer.Exit(code=8) from exc
    curiosity_store().save(item)
    typer.echo(item.as_context())
    typer.echo("Question only. No search, tool call, or memory write was performed.")


@app.command("show")
def show() -> None:
    item = curiosity_store().load()
    typer.echo(item.as_context() if item else "No pending curiosity question.")


@app.command("clear")
def clear() -> None:
    curiosity_store().clear()
    typer.echo("Curiosity question cleared.")


if __name__ == "__main__":
    app()
