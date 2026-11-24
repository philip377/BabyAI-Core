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
