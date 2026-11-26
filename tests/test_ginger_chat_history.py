import pytest

from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommandError
from babyai.workspace_desktop_commands import WorkspaceDesktopCommands


@pytest.mark.parametrize('history_enabled', [False, True])
def test_agent_result_stays_in_its_chat_without_model_or_erp_actions(tmp_path, monkeypatch, history_enabled):
    config = BabyAIConfig(data_dir=tmp_path, provider='echo')
    commands = WorkspaceDesktopCommands(config, persistent=True)
    monkeypatch.setattr(commands, '_core', lambda: pytest.fail('No agent execution'))
    monkeypatch.setattr(commands, '_provider', lambda: pytest.fail('No model call'))
    commands.execute('history.set_enabled', {'enabled': history_enabled})
    chat = commands.execute('chat.current')['active_chat_id']
    commands.execute('ginger.chat.record', {
        'chat_id': chat, 'message': 'Продолжи сопоставление',
        'result': 'Агент остановлен. Сопоставление не выполнялось.'})
    messages = commands.execute('chat.current')['messages']
    assert [(m['role'], m['content']) for m in messages] == [
        ('user', 'Продолжи сопоставление'),
        ('babyai', 'Агент остановлен. Сопоставление не выполнялось.')]
    commands.execute('chat.create')
    assert commands.execute('chat.current')['messages'] == []
    with pytest.raises(DesktopCommandError, match='Чат изменился'):
        commands.execute('ginger.chat.record', {'chat_id': chat, 'message': 'задание', 'result': 'late'})
    commands.execute('chat.select', {'id': chat})
    assert commands.execute('chat.current')['messages'] == messages
    restarted = WorkspaceDesktopCommands(config, persistent=True)
    assert len(restarted.execute('chat.current')['messages']) == (2 if history_enabled else 0)
    assert not config.permissions_file.exists()
    assert not config.lesson_candidate_file.exists()


@pytest.mark.parametrize('payload', [
    {'message': '', 'result': 'done'}, {'message': 'go', 'result': ''},
    {'message': 'x' * 201, 'result': 'done'}, {'message': 'go', 'result': 'x' * 60001},
    {'message': ['go'], 'result': 'done'},
])
def test_invalid_result_does_not_append_messages(tmp_path, payload):
    commands = WorkspaceDesktopCommands(BabyAIConfig(data_dir=tmp_path, provider='echo'))
    chat = commands.execute('chat.current')['active_chat_id']
    with pytest.raises(DesktopCommandError):
        commands.execute('ginger.chat.record', dict(payload, chat_id=chat))
    assert commands.execute('chat.current')['messages'] == []
