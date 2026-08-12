import typer

from .config import BabyAIConfig
from .identity import Identity, IdentityStore
from .llm import EchoProvider
from .memory import MemoryKind, SQLiteMemoryStore
from .primus import Primus

app = typer.Typer(help="BabyAI Core Engine")


def build_core(owner: str | None = None) -> Primus:
    config = BabyAIConfig.default()
    identity_store = IdentityStore(config.identity_file)
    identity = identity_store.load_or_create(
        Identity(name=config.name, owner=owner or config.owner)
    )
    memory = SQLiteMemoryStore(config.memory_db)
    return Primus(llm=EchoProvider(), memory=memory, identity=identity)


@app.command()
def chat(
    message: str | None = typer.Argument(default=None),
    owner: str | None = typer.Option(None, help="Owner name used when identity is first created."),
) -> None:
    """Send one message, or start an interactive session when MESSAGE is omitted."""
    core = build_core(owner)
    if message is not None:
        typer.echo(core.think(message))
        return

    typer.echo(f"{core.identity.name} {core.identity.version}. Type /exit to leave.")
    while True:
        user_input = typer.prompt("you")
        if user_input.strip().lower() in {"/exit", "/quit"}:
            break
        typer.echo(f"babyai> {core.think(user_input)}")


@app.command()
def remember(
    text: str,
    kind: MemoryKind = typer.Option(MemoryKind.FACT, help="Memory type."),
) -> None:
    """Store an explicit durable memory."""
    config = BabyAIConfig.default()
    memory = SQLiteMemoryStore(config.memory_db)
    record = memory.add("owner", text, kind=kind)
    typer.echo(f"remembered #{record.id} [{record.kind.value}]")


@app.command()
def memories(
    query: str | None = typer.Argument(default=None),
    kind: MemoryKind | None = typer.Option(None, help="Filter by memory type."),
    limit: int = typer.Option(20, min=1, max=200),
) -> None:
    """Inspect recent memories or search them by text."""
    config = BabyAIConfig.default()
    memory = SQLiteMemoryStore(config.memory_db)
    records = (
        memory.search(query, limit=limit, kind=kind)
        if query
        else memory.recent(limit=limit, kind=kind)
    )
    for item in records:
        typer.echo(
            f"#{item.id} [{item.kind.value}] {item.role}: {item.content}"
        )


@app.command()
def identity() -> None:
    """Show the persisted BabyAI identity."""
    config = BabyAIConfig.default()
    stored = IdentityStore(config.identity_file).load_or_create(
        Identity(name=config.name, owner=config.owner)
    )
    typer.echo(
        f"name={stored.name}\nowner={stored.owner}\nversion={stored.version}\npurpose={stored.purpose}"
    )


if __name__ == "__main__":
    app()
