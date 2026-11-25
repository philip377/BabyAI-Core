import pytest
from babyai.ginger_answer_reward import record_answer_rating


def report():
    return {'request': {'source': 'product', 'candidates': ['A']},
            'result': {'candidate': None, 'comparisons': [{'status': 'unknown'}]}}


def test_maximum_rating_preserves_abstention_and_does_not_grant_permissions(tmp_path):
    result = record_answer_rating(tmp_path, report(), points=10, user_feedback='Ответ правильный')
    assert result['points'] == result['maximum_points'] == 10
    assert result['evidence']['result']['candidate'] is None
    assert result['attribute_labels_confirmed'] is False
    assert result['automatic_ginger_write_allowed'] is False
    assert result['weights_updated'] is False


def test_repeat_does_not_farm_points(tmp_path):
    first = record_answer_rating(tmp_path, report(), points=10, user_feedback='Верно')
    second = record_answer_rating(tmp_path, report() | {'elapsed_seconds': 9}, points=10, user_feedback='Верно')
    assert first == second
    assert len(list(tmp_path.iterdir())) == 1
    with pytest.raises(ValueError):
        record_answer_rating(tmp_path, report(), points=1, user_feedback='Изменение')


@pytest.mark.parametrize('points', [-1, 11, True, '10', 1.5])
def test_invalid_rating(tmp_path, points):
    with pytest.raises(ValueError):
        record_answer_rating(tmp_path, report(), points=points, user_feedback='Верно')
    assert not list(tmp_path.iterdir())


def test_error_is_not_a_completed_answer(tmp_path):
    with pytest.raises(ValueError):
        record_answer_rating(tmp_path, report() | {'result': {'error': 'timeout'}}, points=10, user_feedback='Верно')

def test_revocation_preserves_evidence_and_excludes_rating(tmp_path):
    from babyai.ginger_answer_reward import active_answer_ratings, revoke_answer_rating
    result = record_answer_rating(tmp_path, report(), points=10, user_feedback='Верно')
    original = (tmp_path / (result['answer_id'] + '.json')).read_bytes()
    assert len(active_answer_ratings(tmp_path)) == 1
    revoke_answer_rating(tmp_path, result['answer_id'], user_feedback='Отзываю оценку')
    assert active_answer_ratings(tmp_path) == []
    assert (tmp_path / (result['answer_id'] + '.json')).read_bytes() == original
    record_answer_rating(tmp_path, report(), points=10, user_feedback='Верно')
    assert active_answer_ratings(tmp_path) == []
    revoke_answer_rating(tmp_path, result['answer_id'], user_feedback='Повтор')
    assert len(list(tmp_path.iterdir())) == 2


@pytest.mark.parametrize('key', ['../outside', '', 'A' * 64, '0' * 63])
def test_revocation_rejects_invalid_identifier(tmp_path, key):
    from babyai.ginger_answer_reward import revoke_answer_rating
    with pytest.raises(ValueError):
        revoke_answer_rating(tmp_path, key, user_feedback='Отзыв')
    assert not list(tmp_path.iterdir())

def test_desktop_rating_and_revocation_without_model_or_actions(tmp_path, monkeypatch):
    from babyai.config import BabyAIConfig
    from babyai.desktop_commands import DesktopCommands
    from babyai.ginger_answer_reward import active_answer_ratings
    commands = DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider='echo'))
    monkeypatch.setattr(commands, '_provider', lambda: pytest.fail('No model call'))
    monkeypatch.setattr(commands, '_core', lambda: pytest.fail('No agent actions'))
    saved = commands.execute('ginger.answer.rate', dict(report=report(), points=10, feedback='Верно'))
    directory = tmp_path / 'ginger-answer-ratings'
    assert len(active_answer_ratings(directory)) == 1
    commands.execute('ginger.answer.revoke', dict(answer_id=saved['answer_id'], feedback='Отзываю'))
    assert active_answer_ratings(directory) == []
    assert list(tmp_path.iterdir()) == [directory]
