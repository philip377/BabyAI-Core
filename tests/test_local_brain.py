import io
import json
import urllib.error

import pytest
from typer.testing import CliRunner

from babyai.cli import app
from babyai.llm import LLMError, OllamaProvider


class FakeResponse:
    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def read(self) -> bytes:
        return json.dumps({"response": "hello from local brain"}).encode()


def test_ollama_provider_parses_response(monkeypatch) -> None:
    monkeypatch.setattr("urllib.request.urlopen", lambda *args, **kwargs: FakeResponse())
    provider = OllamaProvider(model="test")
    assert provider.generate("hello") == "hello from local brain"


def test_ollama_provider_reports_connection_error(monkeypatch) -> None:
    def fail(*args, **kwargs):
        raise urllib.error.URLError("offline")

    monkeypatch.setattr("urllib.request.urlopen", fail)
    with pytest.raises(LLMError, match="Cannot reach local Ollama"):
        OllamaProvider().generate("hello")


def test_cli_smoke_with_echo_provider(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_PROVIDER", "echo")
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    result = CliRunner().invoke(app, ["chat", "hello"])
    assert result.exit_code == 0
    assert "USER: hello" in result.stdout


def test_doctor_echo_provider(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_PROVIDER", "echo")
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    result = CliRunner().invoke(app, ["doctor"])
    assert result.exit_code == 0
    assert "brain=ok" in result.stdout
