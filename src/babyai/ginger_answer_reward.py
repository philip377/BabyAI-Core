"""Human ratings of model answers, separate from verified ERP action outcomes.

Not exposed as an agent tool. Ratings are training evidence, not weight updates
or permission to act. A positive answer rating does not label every attribute.
"""
import hashlib
import json
import os
import re
import tempfile
from datetime import datetime, timezone
from pathlib import Path


def _publish_record(path, record):
    """Publish complete evidence without replacing another writer's record."""
    payload = json.dumps(record, ensure_ascii=False, indent=2, allow_nan=False)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8',
                                         dir=path.parent, suffix='.pending',
                                         delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        # Same-volume hard link publishes the completed file atomically and
        # fails if evidence already exists. Never fall back to overwriting it.
        os.link(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def _answer_path(directory, answer_id):
    if not isinstance(answer_id, str) or not re.fullmatch(r'[0-9a-f]{64}', answer_id):
        raise ValueError("Неверный идентификатор ответа.")
    return Path(directory) / (answer_id + '.json')


def revoke_answer_rating(directory, answer_id, *, user_feedback):
    """Keep original evidence; a separate tombstone excludes it from active ratings."""
    if not isinstance(user_feedback, str) or not user_feedback.strip() or len(user_feedback) > 4000:
        raise ValueError("Нужна причина отзыва оценки.")
    path = _answer_path(directory, answer_id)
    original = json.loads(path.read_text(encoding='utf-8'))
    if original.get('answer_id') != answer_id:
        raise ValueError("Идентификатор записи не совпадает.")
    tombstone = path.with_suffix('.revoked.json')
    record = dict(answer_id=answer_id, user_feedback=user_feedback,
                  recorded_at=datetime.now(timezone.utc).isoformat())
    try:
        _publish_record(tombstone, record)
    except FileExistsError:
        return json.loads(tombstone.read_text(encoding='utf-8'))
    return record


def active_answer_ratings(directory):
    """Return reviewed evidence only; revocation never alters action permissions."""
    records = []
    for path in sorted(Path(directory).glob('*.json')):
        if path.name.endswith('.revoked.json'):
            continue
        _answer_path(directory, path.stem)
        if path.with_suffix('.revoked.json').exists():
            continue
        record = json.loads(path.read_text(encoding='utf-8'))
        if record.get('answer_id') != path.stem:
            raise ValueError("Идентификатор записи не совпадает.")
        records.append(record)
    return records


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
        _publish_record(path, record)
    except FileExistsError:
        previous = json.loads(path.read_text(encoding='utf-8'))
        if previous['points'] != points or previous['user_feedback'] != user_feedback:
            raise ValueError("Ответ уже оценён; изменение требует отдельного отзыва.")
        return previous
    return record
