import pytest
from babyai.ginger_volume_evidence import review_volume


@pytest.mark.parametrize('source,target,ml', [
    ('Acme 100мл EDP', 'Acme edp 100ml', '100'),
    ('Acme 7,5 мл EDP', 'Acme EDP 7.50ml', '7.5'),
    ('Acme 0.1L EDP', 'Acme 100 мл EDP', '100'),
    ('Acme 1 л', 'Acme 1000ML', '1000'),
    ('Acme 30 ml.', 'Acme (30мл)', '30'),
])
def test_explicit_units_and_decimal_quantities(source, target, ml):
    result = review_volume(source, [target])
    assert result['source_measure']['millilitres'] == ml
    assert result['candidates'][0]['measure']['millilitres'] == ml
    assert result['candidates'][0]['volume'] == 'match'
    assert result['can_match'] is False


@pytest.mark.parametrize('notation', [
    '2x30ml', '2 х 30мл', '30ml x 2', '30ml*2', '30ml / 2',
    '30ml (x2)', '2x(30ml)', '2 × (30ml)', '30ml 2-pack',
    '30ml+30ml', '30ml + 30ml', '30ml 30ml', '100/50ml', '30-50ml',
    'набор 30ml', 'set 30ml', '2 шт. 30ml', '30ml 2pcs', '2 bottles 30ml',
    'примерно 100ml', '~100ml', '100ml ±5', 'about 100ml', '100ml approx.',
    '-100ml', '0ml', '100', 'EDP100ml', '100ml2', '.5ml', '1e3ml',
    '1,234.5ml', '1 250ml', '100mi',
])
def test_composite_absent_or_uncertain_volume_never_becomes_a_single_bottle(notation):
    title = 'Acme Bloom ' + notation
    for result in (review_volume(title, ['Acme 100ml']), review_volume('Acme 100ml', [title])):
        assert result['candidates'][0]['volume'] == 'unknown'


def test_different_explicit_sizes_and_evidence_are_preserved():
    result = review_volume('Acme 100 мл', ['Acme 50ml', 'Acme 0,05 l', 'Acme 50ml'])
    assert result['source_measure']['evidence'] == ['100 мл']
    assert [x['volume'] for x in result['candidates']] == ['conflict'] * 3
    assert result['candidates'][1]['measure']['evidence'] == ['0,05 l']
    assert [x['candidate'] for x in result['candidates']] == ['Acme 50ml', 'Acme 0,05 l', 'Acme 50ml']


@pytest.mark.parametrize('source,candidates', [
    ('', ['a']), (None, ['a']), ('a' * 2001, ['a']), ('a', []), ('a', 'b'),
    ('a', ['a'] * 11), ('a', [None]), ('a', [' ']), ('a', ['a' * 2001]),
])
def test_invalid_or_unbounded_inputs_are_rejected(source, candidates):
    with pytest.raises(ValueError):
        review_volume(source, candidates)
