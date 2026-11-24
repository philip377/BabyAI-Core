"""Human ratings of model answers, separate from verified ERP action outcomes.

Not exposed as an agent tool. Ratings are training evidence, not weight updates
or permission to act. A positive answer rating does not label every attribute.
"""
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


def record_answer_rating(directory, report, *, points, user_feedback):
    if type(points) is not int or not 0 <= points <= 10:
        raise ValueError("Оценка должна быть целым числом от 0 до 10.")
    if not isinstance(user_feedback, str) or not user_feedback.strip() or len(user_feedback) > 4000:
        raise ValueError("Нужен явный отзыв пользователя.")
    if not isinstance(report, dict) or not isinstance(report.get('request'), dict) or not isinstance(report.get('result'), dict):
        raise ValueError("Нужны исходный запрос и полный ответ модели.")
    if 'error' in report['result'] or 'candidate' not in report['result']:
        raise ValueError("Нет завершённого ответа для оценки.")
    # The same answer cannot earn points again by changing elapsed time or path.
    evidence = dict(request=report['request'], result=report['result'])
    encoded = json.dumps(evidence, ensure_ascii=False, sort_keys=True, allow_nan=False)
    key = hashlib.sha256(encoded.encode('utf-8')).hexdigest()
    path = Path(directory) / (key + '.json')
    record = dict(schema=1, answer_id=key, scope='answer_outcome', points=points,
                  maximum_points=10, user_feedback=user_feedback,
                  evidence=evidence, attribute_labels_confirmed=False,
                  automatic_ginger_write_allowed=False, weights_updated=False,
                  recorded_at=datetime.now(timezone.utc).isoformat())
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        with path.open('x', encoding='utf-8') as stream:
            json.dump(record, stream, ensure_ascii=False, indent=2, allow_nan=False)
    except FileExistsError:
        previous = json.loads(path.read_text(encoding='utf-8'))
        if previous['points'] != points or previous['user_feedback'] != user_feedback:
            raise ValueError("Ответ уже оценён; изменение требует отдельного отзыва.")
        return previous
    return record
