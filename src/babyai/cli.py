import typer

from .agent import AgentExecutor
from .cognition import Cognition, CognitionProtocolError, TaskProposalStore
from .config import BabyAIConfig
from .hypothesis import Hypothesis, HypothesisProtocolError, HypothesisStore
from .identity import Identity, IdentityStore
from .llm import EchoProvider, LLMError, LLMProvider, OllamaProvider
from .memory import MemoryKind, SQLiteMemoryStore
from .observer import Observer
from .permissions import Capability, PermissionStore
from .planner import Planner
from .primus import Primus
from .tools import Toolset
from .working_memory import TaskState, WorkingMemoryStore

app = typer.Typer(help="BabyAI Core Engine")
permissions_app = typer.Typer(help="Manage BabyAI capabilities")
task_app = typer.Typer(help="Manage BabyAI working task state")
hypothesis_app = typer.Typer(help="Manage explicit testable hypotheses")
app.add_typer(permissions_app, name="permissions")
app.add_typer(task_app, name="task")
app.add_typer(hypothesis_app, name="hypothesis")


def build_provider(config: BabyAIConfig) -> LLMProvider:
    if config.provider == "echo":
        return EchoProvider()
    if config.provider == "ollama":
        return OllamaProvider(model=config.model, base_url=config.ollama_url)
    raise typer.BadParameter(
        f"Unknown BABYAI_PROVIDER={config.provider!r}. Use 'ollama' or 'echo'."
    )


def permission_store() -> PermissionStore:
    return PermissionStore(BabyAIConfig.default().permissions_file)


def working_memory_store() -> WorkingMemoryStore:
    return WorkingMemoryStore(BabyAIConfig.default().working_memory_file)


def task_proposal_store() -> TaskProposalStore:
    return TaskProposalStore(BabyAIConfig.default().task_proposal_file)


def hypothesis_store() -> HypothesisStore:
    return HypothesisStore(BabyAIConfig.default().hypothesis_file)


def build_core(owner: str | None = None) -> Primus:
    config = BabyAIConfig.default()
    identity_store = IdentityStore(config.identity_file)
    identity = identity_store.load_or_create(
        Identity(name=config.name, owner=owner or config.owner)
    )
    memory = SQLiteMemoryStore(config.memory_db)
    permissions = PermissionStore(config.permissions_file)
    return Primus(
        llm=build_provider(config),
        memory=memory,
        identity=identity,
        agent=AgentExecutor(permissions),
        planner=Planner(),
        working_memory=WorkingMemoryStore(config.working_memory_file),
    )


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
    core = build_core(owner)
    if message is not None:
        typer.echo(ask(core, message))
        return
    config = BabyAIConfig.default()
    typer.echo(f"{core.identity.name} {core.identity.version} | brain={config.provider}:{config.model}. Type /exit to leave.")
    while True:
        user_input = typer.prompt("you")
        if user_input.strip().lower() in {"/exit", "/quit"}:
            break
        typer.echo(f"babyai> {ask(core, user_input)}")


@app.command()
def doctor() -> None:
    config = BabyAIConfig.default()
    typer.echo(f"data_dir={config.data_dir}")
    typer.echo(f"provider={config.provider}")
    typer.echo(f"model={config.model}")
    typer.echo(f"memory={config.memory_db}")
    typer.echo(f"working_memory={config.working_memory_file}")
    typer.echo(f"task_proposal={config.task_proposal_file}")
    typer.echo(f"hypothesis={config.hypothesis_file}")
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


@hypothesis_app.command("propose")
def hypothesis_propose(question: str, context: str = typer.Option("")) -> None:
    config = BabyAIConfig.default()
    try:
        record = Hypothesis(build_provider(config)).propose(question, context)
    except (LLMError, HypothesisProtocolError) as exc:
        typer.echo(f"Could not create hypothesis: {exc}", err=True)
        raise typer.Exit(code=5) from exc
    HypothesisStore(config.hypothesis_file).save(record)
    typer.echo(record.as_context())
    typer.echo("No test was executed. Verify it explicitly before changing status.")


@hypothesis_app.command("show")
def hypothesis_show() -> None:
    record = hypothesis_store().load()
    typer.echo(record.as_context() if record else "No stored hypothesis.")


@hypothesis_app.command("confirm")
def hypothesis_confirm() -> None:
    try:
        record = hypothesis_store().set_status("confirmed")
    except ValueError as exc:
        typer.echo(str(exc), err=True)
        raise typer.Exit(code=5) from exc
    typer.echo(record.as_context())


@hypothesis_app.command("reject")
def hypothesis_reject() -> None:
    try:
        record = hypothesis_store().set_status("rejected")
    except ValueError as exc:
        typer.echo(str(exc), err=True)
        raise typer.Exit(code=5) from exc
    typer.echo(record.as_context())


@hypothesis_app.command("clear")
def hypothesis_clear() -> None:
    hypothesis_store().clear()
    typer.echo("Hypothesis cleared.")


@task_app.command("set")
def task_set(
    goal: str,
    status: str = typer.Option("active"),
    context: str = typer.Option(""),
) -> None:
    task = working_memory_store().save(TaskState(goal=goal, status=status, context=context))
    task_proposal_store().clear()
    typer.echo(task.as_context())


@task_app.command("show")
def task_show() -> None:
    task = working_memory_store().load()
    typer.echo(task.as_context() if task else "No active task.")


@task_app.command("clear")
def task_clear() -> None:
    working_memory_store().clear()
    task_proposal_store().clear()
    typer.echo("Task cleared.")


@task_app.command("propose")
def task_propose(observation: str) -> None:
    config = BabyAIConfig.default()
    task = WorkingMemoryStore(config.working_memory_file).load()
    if task is None:
        typer.echo("No active task.", err=True)
        raise typer.Exit(code=4)
    try:
        proposal = Cognition(build_provider(config)).propose(task, observation)
    except (LLMError, CognitionProtocolError) as exc:
        typer.echo(f"Could not create task proposal: {exc}", err=True)
        raise typer.Exit(code=4) from exc
    TaskProposalStore(config.task_proposal_file).save(proposal)
    typer.echo(proposal.as_context())
    typer.echo("Pending only. Run 'babyai task apply' to accept it.")


@task_app.command("proposal")
def task_proposal_show() -> None:
    proposal = task_proposal_store().load()
    typer.echo(proposal.as_context() if proposal else "No pending task proposal.")


@task_app.command("apply")
def task_apply() -> None:
    proposal_store = task_proposal_store()
    proposal = proposal_store.load()
    if proposal is None:
        typer.echo("No pending task proposal.", err=True)
        raise typer.Exit(code=4)
    task = working_memory_store().save(
        TaskState(goal=proposal.goal, status=proposal.status, context=proposal.context)
    )
    proposal_store.clear()
    typer.echo(task.as_context())


@task_app.command("reject")
def task_reject() -> None:
    task_proposal_store().clear()
    typer.echo("Pending task proposal rejected.")


@permissions_app.command("list")
def permissions_list() -> None:
    store = permission_store()
    for capability in Capability:
        state = "granted" if store.is_granted(capability) else "denied"
        typer.echo(f"{capability.value}: {state}")


@permissions_app.command("grant")
def permissions_grant(capability: Capability) -> None:
    permission_store().grant(capability)
    typer.echo(f"granted {capability.value}")


@permissions_app.command("revoke")
def permissions_revoke(capability: Capability) -> None:
    permission_store().revoke(capability)
    typer.echo(f"revoked {capability.value}")


@app.command()
def observe() -> None:
    try:
        snapshot = Observer(permission_store()).system_snapshot()
    except PermissionError as exc:
        typer.echo(str(exc), err=True)
        raise typer.Exit(code=3) from exc
    typer.echo(snapshot.as_context())


@app.command("ls")
def list_directory(path: str = ".") -> None:
    try:
        for item in Toolset(permission_store()).list_directory(path):
            typer.echo(item)
    except (PermissionError, OSError) as exc:
        typer.echo(str(exc), err=True)
        raise typer.Exit(code=3) from exc


@app.command("cat")
def read_file(path: str) -> None:
    try:
        typer.echo(Toolset(permission_store()).read_text(path))
    except (PermissionError, OSError, ValueError) as exc:
        typer.echo(str(exc), err=True)
        raise typer.Exit(code=3) from exc


@app.command()
def remember(text: str, kind: MemoryKind = typer.Option(MemoryKind.FACT)) -> None:
    config = BabyAIConfig.default()
    record = SQLiteMemoryStore(config.memory_db).add("owner", text, kind=kind)
    typer.echo(f"remembered #{record.id} [{record.kind.value}]")


@app.command()
def memories(
    query: str | None = typer.Argument(default=None),
    kind: MemoryKind | None = typer.Option(None),
    limit: int = typer.Option(20, min=1, max=200),
) -> None:
    config = BabyAIConfig.default()
    memory = SQLiteMemoryStore(config.memory_db)
    records = memory.search(query, limit=limit, kind=kind) if query else memory.recent(limit=limit, kind=kind)
    for item in records:
        typer.echo(f"#{item.id} [{item.kind.value}] {item.role}: {item.content}")


@app.command()
def identity() -> None:
    config = BabyAIConfig.default()
    stored = IdentityStore(config.identity_file).load_or_create(Identity(name=config.name, owner=config.owner))
    typer.echo(f"name={stored.name}\nowner={stored.owner}\nversion={stored.version}\npurpose={stored.purpose}")


if __name__ == "__main__":
    app()
