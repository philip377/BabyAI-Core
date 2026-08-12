import typer

from .config import BabyAIConfig
from .identity import Identity, IdentityStore
from .llm import EchoProvider, LLMError, LLMProvider, OllamaProvider
from .memory import MemoryKind, SQLiteMemoryStore
from .observer import Observer
from .permissions import Capability, PermissionStore
from .primus import Primus
from .tools import Toolset

app = typer.Typer(help="BabyAI Core Engine")
permissions_app = typer.Typer(help="Manage BabyAI capabilities")
app.add_typer(permissions_app, name="permissions")


def build_provider(config: BabyAIConfig) -> LLMProvider:
    if config.provider == "echo":
        return EchoProvider()
    if config.provider == "ollama":
        return OllamaProvider(model=config.model, base_url=config.ollama_url)
    raise typer.BadParameter(
        f"Unknown BABYAI_PROVIDER={config.provider!r}. Use 'ollama' or 'echo'."
    )


def build_core(owner: str | None = None) -> Primus:
    config = BabyAIConfig.default()
    identity_store = IdentityStore(config.identity_file)
    identity = identity_store.load_or_create(
        Identity(name=config.name, owner=owner or config.owner)
    )
    memory = SQLiteMemoryStore(config.memory_db)
    return Primus(llm=build_provider(config), memory=memory, identity=identity)


def permission_store() -> PermissionStore:
    return PermissionStore(BabyAIConfig.default().permissions_file)


def ask(core: Primus, message: str) -> str:
    try:
        return core.think(message)
    except LLMError as exc:
        typer.echo(f"Local brain unavailable: {exc}", err=True)
        raise typer.Exit(code=2) from exc


@app.command()
def chat(
    message: str | None = typer.Argument(default=None),
    owner: str | None = typer.Option(None, help="Owner name used when identity is first created."),
) -> None:
    """Send one message, or start an interactive session when MESSAGE is omitted."""
    core = build_core(owner)
    if message is not None:
        typer.echo(ask(core, message))
        return

    config = BabyAIConfig.default()
    typer.echo(
        f"{core.identity.name} {core.identity.version} | brain={config.provider}:{config.model}. "
        "Type /exit to leave."
    )
    while True:
        user_input = typer.prompt("you")
        if user_input.strip().lower() in {"/exit", "/quit"}:
            break
        typer.echo(f"babyai> {ask(core, user_input)}")


@app.command()
def doctor() -> None:
    """Show local BabyAI configuration and test the configured brain."""
    config = BabyAIConfig.default()
    typer.echo(f"data_dir={config.data_dir}")
    typer.echo(f"provider={config.provider}")
    typer.echo(f"model={config.model}")
    typer.echo(f"memory={config.memory_db}")
    typer.echo(f"permissions={config.permissions_file}")
    if config.provider == "echo":
        typer.echo("brain=ok (echo diagnostics provider)")
        return
    provider = build_provider(config)
    try:
        provider.generate("Reply with exactly: OK")
    except LLMError as exc:
        typer.echo(f"brain=unavailable ({exc})", err=True)
        raise typer.Exit(code=2) from exc
    typer.echo("brain=ok")


@permissions_app.command("list")
def permissions_list() -> None:
    """List all capabilities and whether they are granted."""
    store = permission_store()
    for capability in Capability:
        state = "granted" if store.is_granted(capability) else "denied"
        typer.echo(f"{capability.value}: {state}")


@permissions_app.command("grant")
def permissions_grant(capability: Capability) -> None:
    """Explicitly grant one capability."""
    permission_store().grant(capability)
    typer.echo(f"granted {capability.value}")


@permissions_app.command("revoke")
def permissions_revoke(capability: Capability) -> None:
    """Revoke one capability."""
    permission_store().revoke(capability)
    typer.echo(f"revoked {capability.value}")


@app.command()
def observe() -> None:
    """Capture a permissioned system snapshot."""
    try:
        snapshot = Observer(permission_store()).system_snapshot()
    except PermissionError as exc:
        typer.echo(str(exc), err=True)
        raise typer.Exit(code=3) from exc
    typer.echo(snapshot.as_context())


@app.command("ls")
def list_directory(path: str = ".") -> None:
    """List a directory through the permissioned tool layer."""
    try:
        for item in Toolset(permission_store()).list_directory(path):
            typer.echo(item)
    except (PermissionError, OSError) as exc:
        typer.echo(str(exc), err=True)
        raise typer.Exit(code=3) from exc


@app.command("cat")
def read_file(path: str) -> None:
    """Read a text file through the permissioned tool layer."""
    try:
        typer.echo(Toolset(permission_store()).read_text(path))
    except (PermissionError, OSError, ValueError) as exc:
        typer.echo(str(exc), err=True)
        raise typer.Exit(code=3) from exc


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
    records = memory.search(query, limit=limit, kind=kind) if query else memory.recent(limit=limit, kind=kind)
    for item in records:
        typer.echo(f"#{item.id} [{item.kind.value}] {item.role}: {item.content}")


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
