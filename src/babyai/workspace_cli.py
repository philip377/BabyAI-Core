from __future__ import annotations

import typer

from .config import BabyAIConfig
from .workspace import WorkspaceStore


app = typer.Typer(help="Manage UNIX workspaces without granting filesystem access.")


def workspace_store() -> WorkspaceStore:
    return WorkspaceStore(BabyAIConfig.default().workspace_file)


@app.command("create")
def create_workspace(
    name: str,
    root: str | None = typer.Option(
        None,
        help="Optional existing project root. Stored as metadata only; no files are read.",
    ),
    select: bool = typer.Option(
        True,
        "--select/--no-select",
        help="Make the new workspace active immediately.",
    ),
) -> None:
    store = workspace_store()
    try:
        workspace = store.create(name, root=root)
        if select:
            store.select(workspace.id)
    except (ValueError, FileNotFoundError, NotADirectoryError) as exc:
        typer.echo(str(exc), err=True)
        raise typer.Exit(code=2) from exc
    typer.echo(f"Created workspace: {workspace.name} [{workspace.id}]")
    if workspace.root:
        typer.echo(f"root={workspace.root} (metadata only)")
    if select:
        typer.echo("active=true")


@app.command("list")
def list_workspaces() -> None:
    store = workspace_store()
    try:
        active = store.active()
        workspaces = store.list()
    except ValueError as exc:
        typer.echo(str(exc), err=True)
        raise typer.Exit(code=2) from exc
    if not workspaces:
        typer.echo("No workspaces.")
        return
    active_id = None if active is None else active.id
    for workspace in workspaces:
        marker = "*" if workspace.id == active_id else " "
        root = f" · {workspace.root}" if workspace.root else ""
        typer.echo(f"{marker} {workspace.name} [{workspace.id}]{root}")


@app.command("select")
def select_workspace(identifier: str) -> None:
    try:
        workspace = workspace_store().select(identifier)
    except (ValueError, KeyError) as exc:
        typer.echo(str(exc), err=True)
        raise typer.Exit(code=2) from exc
    typer.echo(f"Active workspace: {workspace.name} [{workspace.id}]")


@app.command("current")
def current_workspace() -> None:
    try:
        workspace = workspace_store().active()
    except ValueError as exc:
        typer.echo(str(exc), err=True)
        raise typer.Exit(code=2) from exc
    if workspace is None:
        typer.echo("No active workspace.")
        return
    typer.echo(workspace.as_context())
    typer.echo(f"id={workspace.id}")


@app.command("clear")
def clear_workspace() -> None:
    workspace_store().clear_active()
    typer.echo("Active workspace cleared.")
