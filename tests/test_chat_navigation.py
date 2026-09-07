import sqlite3
from pathlib import Path
import pytest
from babyai.agent_desktop import AgentDesktopCommands
from babyai.config import BabyAIConfig
from babyai.history import ChatHistoryStore


def test_chat_switch_restores_context_and_selection_after_worker_restart(tmp_path):
    config = BabyAIConfig(data_dir=tmp_path, provider='echo')
    commands = AgentDesktopCommands(config, persistent=True)
    commands.execute('history.set_enabled', {'enabled': True})
    first = commands.execute('chat.current')['active_chat_id']
    commands.stream_chat({'message': 'Alpha secret'}, lambda event: None)
    second = commands.execute('chat.create')['active_chat_id']
    assert second != first
    assert commands._session_store().recent() == []
    commands.execute('chat', {'message': 'Beta text'})
    selected = commands.execute('chat.select', {'id': first})
    assert selected['messages'][0]['content'] == 'Alpha secret'
    assert all('Beta text' not in item.content for item in commands._session_store().recent())
    restarted = AgentDesktopCommands(config, persistent=True)
    assert restarted.execute('chat.current')['active_chat_id'] == first
    assert restarted._session_store().recent()[0].content == 'Alpha secret'
    assert any(item['title'] == 'Alpha secret' for item in selected['chats'])


def test_workspace_selection_cannot_expose_another_chat(tmp_path):
    commands = AgentDesktopCommands(BabyAIConfig(data_dir=tmp_path, provider='echo'))
    commands.execute('history.set_enabled', {'enabled': True})
    alpha = commands.execute('workspace.create', {'name': 'Alpha'})['workspace']
    beta = commands.execute('workspace.create', {'name': 'Beta'})['workspace']
    commands.execute('workspace.select', {'id': alpha['id']})
    first = commands.execute('chat.current')['active_chat_id']
    commands.execute('chat', {'message': 'Private Alpha'})
    commands.execute('workspace.select', {'id': beta['id']})
    assert commands.execute('chat.current')['messages'] == []
    with pytest.raises(ValueError, match='active workspace'):
        commands.execute('chat.select', {'id': first})
    commands.execute('workspace.select', {'id': alpha['id']})
    assert commands.execute('chat.current')['active_chat_id'] == first


def test_disabled_history_does_not_persist_text_or_derived_title(tmp_path):
    config = BabyAIConfig(data_dir=tmp_path, provider='echo')
    commands = AgentDesktopCommands(config)
    first = commands.execute('chat.current')['active_chat_id']
    commands.execute('chat', {'message': 'Never persist this'})
    commands.execute('chat.create')
    assert commands.execute('chat.select', {'id': first})['messages'][0]['content'] == 'Never persist this'
    snapshot = AgentDesktopCommands(config).execute('chat.current')
    assert snapshot['messages'] == []
    assert all(item['title'] == 'Новый чат' for item in snapshot['chats'])
    assert ChatHistoryStore(config.history_db, config.history_settings_file).list() == []


def test_legacy_history_migrates_once_in_place(tmp_path):
    store = ChatHistoryStore(tmp_path / 'history.db', tmp_path / 'settings.json')
    with sqlite3.connect(store.db_path) as connection:
        connection.execute('CREATE TABLE history (id INTEGER PRIMARY KEY, project TEXT, role TEXT, content TEXT, created_at TEXT)')
        connection.execute("INSERT INTO history VALUES (1, 'Alpha', 'user', 'Old chat', 'today')")
    first = store.current_chat('alpha-id', 'Alpha')
    assert store.chat_messages(first)[0].id == 1
    second = store.create_chat('alpha-id', 'Alpha')
    assert store.current_chat('alpha-id', 'Alpha') == second
    assert store.chat_messages(second) == []
    assert len(store.list()) == 1


def test_frame_and_navigation_contract():
    root = Path(__file__).resolve().parents[1] / 'desktop/BabyAI.Desktop'
    frame = (root / 'MainWindow.Frame.cs').read_text(encoding='utf-8')
    layout = (root / 'MainWindow.AdaptiveUi.cs').read_text(encoding='utf-8')
    navigation = (root / 'MainWindow.Navigation.cs').read_text(encoding='utf-8')
    assert 'presenter.IsResizable = expanded' in frame
    assert 'WmNcHitTest' in frame and 'WmGetMinMaxInfo' in frame
    assert 'GetDpiForWindow' in frame and 'CreateEllipticRgn' in frame
    assert '0xFFFFFFFE' in frame
    assert 'Panel.Width = double.NaN' in layout
    assert 'AppWindow.MoveAndResize' not in layout
    assert 'NavigationAsync("workspace.list")' in navigation
    assert 'NavigationAsync("chat.current")' in navigation
    assert 'if (_busy) return' in navigation


def test_pending_action_blocks_new_chat_and_selection(tmp_path):
    from babyai.permissions import Capability
    from babyai.tool_approval import PendingToolApproval, PendingToolApprovalStore
    config = BabyAIConfig(data_dir=tmp_path, provider="echo")
    commands = AgentDesktopCommands(config)
    first = commands.execute("chat.current")["active_chat_id"]
    second = commands.execute("chat.create")["active_chat_id"]
    PendingToolApprovalStore(config.pending_tool_approval_file).save(PendingToolApproval(
        user_input="List files", tool="filesystem.list", arguments={"path": "."},
        capability=Capability.FILESYSTEM_LIST.value))
    for command, payload in [("chat.create", {}), ("chat.select", {"id": first})]:
        with pytest.raises(ValueError, match="pending local action"):
            commands.execute(command, payload)
    assert commands.execute("chat.current")["active_chat_id"] == second


def test_clear_history_removes_chat_titles_and_restorable_context(tmp_path):
    commands = AgentDesktopCommands(BabyAIConfig(data_dir=tmp_path, provider="echo"))
    commands.execute("history.set_enabled", {"enabled": True})
    commands.execute("chat", {"message": "Sensitive title"})
    assert commands.execute("history.clear")["deleted"] == 2
    snapshot = commands.execute("chat.current")
    assert snapshot["messages"] == []
    assert all(item["title"] == "Новый чат" for item in snapshot["chats"])
    assert commands._session_store().recent() == []
