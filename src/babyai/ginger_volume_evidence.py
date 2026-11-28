"""Read explicit single-bottle measures; never infer a missing size or select a product."""
from decimal import Decimal
import re


_MEASURE = re.compile(
    r'(?<![\w.,])(?P<amount>\d{1,8}(?:[.,]\d{1,6})?)\s*(?P<unit>ml|мл|l|л)(?!\w)',
    re.IGNORECASE,
)
_COMPOSITE = re.compile(
    r'(?<!\w)(?:набор|комплект|set|kit|multipack|duo|trio)(?!\w)|'
    r'(?<!\w)\d+\s*(?:шт\.?|pcs?\.?|флакон\w*|bottles?|[- ]?packs?)(?!\w)|'
    r'(?<!\w)[xх×*]\s*\d+(?!\w)',
    re.IGNORECASE,
)
_BEFORE_EXPRESSION = re.compile(
    r'(?:[xх×*/+−\-–—~≈±]|(?<!\w)(?:около|примерно|до|от|about|approx\.?)|(?<!\w)\d+\s+)\s*\(?\s*$', re.IGNORECASE)
_AFTER_EXPRESSION = re.compile(
    r'^\s*\(?\s*(?:[xх×*/+−\-–—±]\s*\d|(?:примерно|approx\.?)(?!\w))', re.IGNORECASE)


def _read(title):
    measures = list(_MEASURE.finditer(title))
    evidence = [m.group(0) for m in measures]
    result = dict(evidence=evidence, millilitres=None)
    if _COMPOSITE.search(title):
        return dict(result, reason='Набор или количество флаконов требует отдельной проверки.')
    if len(measures) != 1:
        return dict(result, reason=('Не найден один явный объём в мл или л.' if not measures else
                                    'Указано несколько объёмов; состав не сводится к одному флакону.'))
    measure = measures[0]
    if (_BEFORE_EXPRESSION.search(title[:measure.start()])
            or _AFTER_EXPRESSION.search(title[measure.end():])):
        return dict(result, reason='Объём входит в количество, диапазон или неточное обозначение.')
    value = Decimal(measure['amount'].replace(',', '.'))
    if measure['unit'].casefold() in ('l', 'л'):
        value *= 1000
    if value <= 0:
        return dict(result, reason='Нулевой или некорректный объём нельзя подтвердить.')
    return dict(result, millilitres=format(value.normalize(), 'f'),
                reason='Прочитан один явный объём; мл и л приведены к одной единице.')


def review_volume(source, candidates):
    """Evidence only. Composite/absent/uncertain notation stays unknown, never summed."""
    if (not isinstance(source, str) or not source.strip() or len(source) > 2000
            or not isinstance(candidates, list) or not 1 <= len(candidates) <= 10
            or any(not isinstance(c, str) or not c.strip() or len(c) > 2000 for c in candidates)):
        raise ValueError('Для проверки объёма нужны исходная строка и не более 10 кандидатов.')
    source_measure = _read(source)
    readings = {title: _read(title) for title in dict.fromkeys(candidates)}
    results = []
    for title in candidates:
        measure = readings[title]
        if source_measure['millilitres'] is None or measure['millilitres'] is None:
            status = 'unknown'
            reason = 'Нельзя подтвердить один сопоставимый объём с обеих сторон.'
        elif source_measure['millilitres'] == measure['millilitres']:
            status = 'match'
            reason = 'Явно указанные объёмы совпадают.'
        else:
            status = 'conflict'
            reason = 'Явно указаны разные объёмы.'
        results.append(dict(candidate=title, measure=dict(measure), volume=status, reason=reason))
    return dict(source=source, source_measure=source_measure, candidates=results, can_match=False)
