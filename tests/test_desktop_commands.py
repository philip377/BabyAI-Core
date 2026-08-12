import pytest

from babyai.autodidact import LessonCandidate, LessonCandidateStore
from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommandError, DesktopCommands
from babyai.memory import MemoryKind, SQLiteMemoryStore


def setup_commands(tmp_path):
    config = BabyAIConfig(data_dir=tmp_path, owner="tester", name="BabyAI", provider="echo")
    return config, DesktopCommands(config)


def test_status_is_read_only(tmp_path):
    _, commands = setup_commands(tmp_path)
    before = sorted(p.name for p in tmp_path.iterdir())
    result = commands.execute("status")
    after = sorted(p.name for p in tmp_path.iterdir())
    assert result["snapshot"]["schema_version"] == 1
    assert before == after


def test_task_set_and_clear(tmp_path):
    _, commands = setup_commands(tmp_path)
    result = commands.execute("task.set", {"goal": "Build Orb", "context": "Windows"})
    assert result["task"]["goal"] == "Build Orb"
    commands.execute("task.clear")
    assert commands.execute("status")["snapshot"]["task"] is None


def test_unknown_command_fails_closed(tmp_path):
    _, commands = setup_commands(tmp_path)
    with pytest.raises(DesktopCommandError, match="Unsupported"):
        commands.execute("unknown.command")


def test_lesson_approve_writes_once(tmp_path):
    config, commands = setup_commands(tmp_path)
    with pytest.raises(DesktopCommandError, match="No pending"):
        commands.execute("lesson.approve")
    LessonCandidateStore(config.lesson_candidate_file).save(
        LessonCandidate(knowledge="Keep the bridge narrow", rationale="Safety", source="test")
    )
    commands.execute("lesson.approve")
    records = SQLiteMemoryStore(config.memory_db).recent(limit=10, kind=MemoryKind.KNOWLEDGE)
    assert [item.content for item in records] == ["Keep the bridge narrow"]
    with pytest.raises(DesktopCommandError, match="No pending"):
        commands.execute("lesson.approve")


def test_echo_chat_returns_reply(tmp_path):
    _, commands = setup_commands(tmp_path)
    result = commands.execute("chat", {"message": "hello"})
    assert result["ok"] is True
    assert isinstance(result["reply"], str)
