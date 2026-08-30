from __future__ import annotations

from babyai.agent import ToolCall
from babyai.config import BabyAIConfig
from babyai.identity import Identity
from babyai.truthful_desktop import (
    SessionToolObservationStore,
    TruthfulWorkspaceDesktopCommands,
)


def test_identity_creator_question_is_grounded() -> None:
    identity = Identity()

    reply = identity.provenance_reply("а кем разработан?")

    assert reply is not None
    assert "владелец/разработчик" in reply
    assert "BabyAI Core" in reply
    assert "не является продуктом Anthropic" in reply
    assert "Creator: the BabyAI Core owner/developer" in identity.system_context()


def test_file_followups_only_use_recorded_listing() -> None:
    store = SessionToolObservationStore()
    store.record(
        ToolCall("filesystem.list", {"path": "~/Desktop"}),
        '["BabyAI.lnk", "notes.txt", "report.pdf", "Folder/"]',
    )
    store.mark_consumed_from_reply("Например: BabyAI.lnk")

    assert store.consume_followup("а еще какие?") == "Ещё: notes.txt"
    assert store.consume_followup("а кроме этого файла?") == "Ещё: report.pdf"
    assert (
        store.consume_followup("а еще?")
        == "В сохранённом списке файлов из ~/Desktop больше файлов не было."
    )


def test_file_followup_correction_discards_old_observation() -> None:
    store = SessionToolObservationStore()
    store.record(
        ToolCall("filesystem.list", {"path": "~/Desktop"}),
        '["BabyAI.lnk", "notes.txt"]',
    )
    store.mark_consumed_from_reply("Например: BabyAI.lnk")

    reply = store.consume_followup("да нет там таких файлов")

    assert reply is not None
    assert "не буду утверждать" in reply
    assert store.consume_followup("а ещё какие?") is None


def test_streaming_followup_bypasses_model_and_keeps_protocol(tmp_path) -> None:
    commands = TruthfulWorkspaceDesktopCommands(
        BabyAIConfig(data_dir=tmp_path, provider="echo")
    )
    commands._tool_observations.record(
        ToolCall("filesystem.list", {"path": "~/Desktop"}),
        '["BabyAI.lnk", "notes.txt"]',
    )
    commands._tool_observations.mark_consumed_from_reply("Например: BabyAI.lnk")
    events: list[dict[str, object]] = []

    result = commands.stream_chat({"message": "а еще какие?"}, events.append)

    assert result["reply"] == "Ещё: notes.txt"
    assert result["metrics"]["model_calls"] == 0
    assert result["metrics"]["stop_reason"] == "session_context"
    assert events == [
        {"event": "state", "state": "thinking"},
        {"event": "state", "state": "answering"},
        {"event": "delta", "text": "Ещё: notes.txt"},
    ]


def test_streaming_creator_reply_bypasses_model(tmp_path) -> None:
    commands = TruthfulWorkspaceDesktopCommands(
        BabyAIConfig(data_dir=tmp_path, provider="echo")
    )
    events: list[dict[str, object]] = []

    result = commands.stream_chat({"message": "а кем ты разработан?"}, events.append)

    assert result["metrics"]["model_calls"] == 0
    assert "BabyAI Core" in result["reply"]
    assert "не является продуктом Anthropic" in result["reply"]
