import typer

from .config import BabyAIConfig
from .identity import Identity
from .llm import EchoProvider
from .memory import SQLiteMemoryStore
from .primus import Primus

app = typer.Typer(help="BabyAI Core Engine")


def build_core(owner: str | None = None) -> Primus:
    config = BabyAIConfig.default()
    identity = Identity(name=config.name, owner=owner or config.owner)
    memory = SQLiteMemoryStore(config.memory_db)
    return Primus(llm=EchoProvider(), memory=memory, identity=identity)


@app.command()
def chat(
    message: str | None = typer.Argument(default=None),
    owner: str = typer.Option("owner", help="Owner name used by BabyAI identity."),
) -> None:
    """Send one message, or start an interactive session when MESSAGE is omitted."""
    core = build_core(owner)
    if message is not None:
        typer.echo(core.think(message))
        return

    typer.echo("BabyAI Genesis. Type /exit to leave.")
    while True:
        user_input = typer.prompt("you")
        if user_input.strip().lower() in {"/exit", "/quit"}:
            break
        typer.echo(f"babyai> {core.think(user_input)}")


if __name__ == "__main__":
    app()
