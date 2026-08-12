from __future__ import annotations

import json

import typer

from .desktop_bridge import build_desktop_snapshot

app = typer.Typer(help="Read-only JSON bridge for BabyAI Desktop clients")


@app.command("status")
def status() -> None:
    snapshot = build_desktop_snapshot()
    typer.echo(json.dumps(snapshot.as_dict(), ensure_ascii=False, indent=2))


if __name__ == "__main__":
    app()
