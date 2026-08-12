import typer

from .llm import EchoProvider
from .memory import InMemoryStore
from .primus import Primus

app = typer.Typer(help="BabyAI Core Engine")


@app.command()
def chat(message: str) -> None:
    core = Primus(llm=EchoProvider(), memory=InMemoryStore())
    typer.echo(core.think(message))


if __name__ == "__main__":
    app()
