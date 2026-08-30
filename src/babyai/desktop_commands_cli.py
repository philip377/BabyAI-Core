from __future__ import annotations

import json

import typer

from .desktop_commands import DesktopCommandError
from .truthful_desktop import TruthfulWorkspaceDesktopCommands as DesktopCommands

app = typer.Typer(help="BabyAI Desktop command bridge")


@app.callback()
def main() -> None:
    """Local command bridge used by trusted BabyAI desktop clients."""


@app.command("exec")
def exec_command(command: str, payload: str = typer.Option("{}")) -> None:
    try:
        data = json.loads(payload)
        if not isinstance(data, dict):
            raise ValueError("payload must be a JSON object")
        result = DesktopCommands().execute(command, data)
    except (json.JSONDecodeError, ValueError, DesktopCommandError) as exc:
        typer.echo(
            json.dumps({"ok": False, "error": str(exc)}, ensure_ascii=False),
            err=True,
        )
        raise typer.Exit(code=8) from exc
    typer.echo(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    app()
