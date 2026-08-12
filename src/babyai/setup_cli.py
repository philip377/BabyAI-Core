from __future__ import annotations

import typer

from .cli import build_provider
from .config import BabyAIConfig
from .diagnostics import initialize_data_dir, run_local_diagnostics
from .llm import LLMError

app = typer.Typer(help="Initialize and diagnose a BabyAI installation")


@app.command("init")
def init_command() -> None:
    config = BabyAIConfig.default()
    try:
        created = initialize_data_dir(config)
    except (OSError, ValueError) as exc:
        typer.echo(f"Initialization failed: {exc}", err=True)
        raise typer.Exit(code=8) from exc
    typer.echo("BabyAI initialized.")
    for path in created:
        typer.echo(str(path))
    typer.echo("Permissions remain denied by default.")


@app.command("doctor")
def doctor_command(skip_brain: bool = typer.Option(False, help="Skip the local LLM connectivity check.")) -> None:
    config = BabyAIConfig.default()
    report = run_local_diagnostics(config)
    for check in report.checks:
        state = "ok" if check.ok else "fail"
        typer.echo(f"{check.name}={state} ({check.detail})")

    brain_ok = True
    if skip_brain:
        typer.echo("brain=skipped")
    elif config.provider == "echo":
        typer.echo("brain=ok (echo diagnostics provider)")
    else:
        try:
            build_provider(config).generate("Reply with exactly: OK")
            typer.echo(f"brain=ok ({config.provider}:{config.model})")
        except LLMError as exc:
            brain_ok = False
            typer.echo(f"brain=fail ({exc})", err=True)

    if not report.ok or not brain_ok:
        raise typer.Exit(code=8)
    typer.echo("overall=ok")


if __name__ == "__main__":
    app()
