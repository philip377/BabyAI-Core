import json
import pytest
from babyai.ginger_identity_evidence import review_identity


SOURCE = 'Acme Bloom edp 50ml'
A = 'Acme BLOOM edp 50ml'
B = 'Acme BLOOM NIGHT edp 50ml'


class Model:
    def __init__(self, fields=None, verdict='conflict'):
        self.calls = []
        self.fields = fields or {
            SOURCE: dict(brand='Acme', fragrance='Bloom'),
            A: dict(brand='Acme', fragrance='BLOOM'),
            B: dict(brand='Acme', fragrance='BLOOM NIGHT'),
        }
        self.verdict = verdict

    def generate(self, prompt):
        data = json.loads(prompt.split('\n')[-1])
        self.calls.append(data)
        if 'title' in data:
            return json.dumps(self.fields[data['title']])
        return json.dumps(dict(identity=self.verdict, reason='Проверено полное название'))


def test_evidence_keeps_grounded_fields_and_does_not_choose_product():
    model = Model()
    result = review_identity(model, SOURCE, [A, B, A])
    assert result['can_match'] is False and 'candidate' not in result
    assert result['source'] == SOURCE
    assert result['source_fields'] == dict(brand='Acme', fragrance='Bloom')
    assert [c['candidate'] for c in result['candidates']] == [A, B, A]
    assert result['candidates'][1]['candidate_fields']['fragrance'] == 'BLOOM NIGHT'
    assert len(model.calls) == 5  # Unique candidate extracts, source, unique comparisons.
    assert model.calls[2]['catalog_brands'] == ['Acme']
    review_identity(model, SOURCE, [A, B])
    assert len(model.calls) == 10  # No evidence cache across observations.


@pytest.mark.parametrize('source,candidates', [('', [A]), (SOURCE, []), (SOURCE, [A] * 11),
                                           (None, [A]), (SOURCE, [1]), ('x' * 2001, [A])])
def test_invalid_input_never_calls_model(source, candidates):
    model = Model()
    with pytest.raises(ValueError):
        review_identity(model, source, candidates)
    assert model.calls == []


@pytest.mark.parametrize('fields', [
    dict(brand='Invented', fragrance='BLOOM'),
    dict(brand='Ac', fragrance='BLOOM'),  # A fragment inside a word is not a field.
    dict(brand='Acme BLOOM', fragrance='BLOOM'),  # Overlapping fields.
    dict(brand='Acme', fragrance='Bloom'),  # Not the exact casing of this title.
    dict(brand=None, fragrance='BLOOM'),
])
def test_ungrounded_extraction_becomes_unknown_without_comparison(fields):
    model = Model(); model.fields[A] = fields
    result = review_identity(model, SOURCE, [A])
    assert result['candidates'][0]['identity'] == 'unknown'
    assert result['candidates'][0]['candidate_fields'] is None
    assert all('title' in call for call in model.calls)


def test_repeated_field_is_ambiguous():
    title = 'Acme Acme BLOOM edp 50ml'
    model = Model(); model.fields[title] = dict(brand='Acme', fragrance='BLOOM')
    assert review_identity(model, SOURCE, [title])['candidates'][0]['identity'] == 'unknown'


@pytest.mark.parametrize('reply', [[], {'brand': 'Acme'}, {'brand': 1, 'fragrance': 'BLOOM'},
                                 {'brand': '', 'fragrance': 'BLOOM'}, {'brand': 'Acme', 'fragrance': 'BLOOM', 'tool': 'click'}])
def test_invalid_field_schema_is_rejected(reply):
    model = Model(); model.fields[A] = reply
    with pytest.raises(ValueError):
        review_identity(model, SOURCE, [A])


@pytest.mark.parametrize('verdict', ['yes', None, {}, [], True])
def test_invalid_identity_is_rejected(verdict):
    with pytest.raises(ValueError):
        review_identity(Model(verdict=verdict), SOURCE, [A])


def test_brand_clues_are_order_independent_and_not_inserted_in_source():
    other = 'Other DAWN edp 50ml'
    fields = Model().fields | {other: dict(brand='Other', fragrance='DAWN')}
    inputs = []
    for candidates in ([other, A], [A, other]):
        model = Model(fields)
        result = review_identity(model, SOURCE, candidates)
        inputs.append(next(c for c in model.calls if c.get('title') == SOURCE))
        assert result['source_fields']['brand'] == 'Acme'
    assert inputs[0] == inputs[1] == dict(title=SOURCE, catalog_brands=['Acme', 'Other'])
