import pytest
from babyai.ginger_variant_evidence import explain_variants, unresolved_variant
from babyai.ginger_supplier_matcher import CatalogPosition, SupplierPosition, GingerSupplierMatcher, explain_candidate


@pytest.mark.parametrize('suffix', ['EAU INTENSE', 'NOIR POUR HOMME', 'POUR FEMME', 'POUR HOMME'])
def test_short_title_does_not_confirm_variant(suffix):
    source = SupplierPosition('fixture', 'ROJA ELYSIUM edp 100 ml tester')
    target = CatalogPosition('fixture-only', f'ROJA ELYSIUM {suffix} edp 100 ml tester')
    result = GingerSupplierMatcher([target]).recommend(source)
    assert result.status == 'review' and result.matched_sku is None
    assert result.candidates[0].score <= .79
    explanation = explain_candidate(source, target)
    assert any(x['field'] in ('variant_words', 'audience') for x in explanation['uncertainties'])
    assert not explanation['can_match']


def test_explicit_audience_conflict_and_alias_spelling():
    differences, unknowns = explain_variants('A pour homme', 'A (жен)')
    assert differences[0]['field'] == 'audience' and not unknowns
    assert not unresolved_variant('A pour homme', 'A (муж)')


def test_does_not_infer_audience_from_brand_or_missing_words():
    assert explain_variants('MAN TOO edp', 'MAN TOO edp') == ([], [])
    assert not unresolved_variant('Black Orchid', 'Black Orchid')
    assert not unresolved_variant('Noir', 'NOIR')
    assert unresolved_variant('Noir', 'Intense')


def test_ambiguous_audience_is_not_equal_evidence():
    title = 'A pour homme pour femme'
    assert unresolved_variant(title, title)
    assert explain_variants(title, title)[1][0]['field'] == 'audience'


def test_legacy_explanations_unchanged_when_conditions_disabled():
    result = explain_candidate(SupplierPosition('s', 'A edp 100ml'),
                               CatalogPosition('x', 'A NOIR pour homme edp 100ml'),
                               include_conditions=False)
    assert not any(x['field'] in ('variant_words', 'audience')
                   for x in result['differences'] + result['uncertainties'])
