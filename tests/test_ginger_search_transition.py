import copy
import pytest
from babyai.ginger_search_transition import compare_search_transition
from babyai.ginger_price_queue import PriceQueueError


def context(position=1):
    source = {'TableHandle': 100, 'ObservedRowPosition': position, 'DocumentNumber': '17',
              'Supplier': 'Supplier', 'Title': 'Same source', 'PriceText': '10', 'LinkedProductText': '<none>',
              'IdentityVerified': False, 'CanMatch': False}
    result = {'CanMatch': False, 'SearchCompletionVerified': False}
    for index, name in enumerate(('SearchWords', 'Products'), 200):
        result[name] = {'Source': copy.deepcopy(source), 'PanelHandle': index,
            'CanMatch': False, 'IdentityVerified': False, 'SearchCompletionVerified': False,
            'Candidates': [{'ObservedPosition': 1, 'Title': 'Same text', 'DescriptionText': 'Details',
                            'Selected': False, 'Checked': None, 'IdentityVerified': False}]}
    return result


@pytest.mark.parametrize('changed', ['none', 'query', 'candidate', 'both', 'checkbox', 'selection'])
def test_changes_never_certify_freshness(changed):
    before, after = context(), context(2)
    if changed in ('query', 'both'): after['SearchWords']['Candidates'][0]['Title'] = 'New query'
    if changed in ('candidate', 'both'): after['Products']['Candidates'][0]['Title'] = 'New candidate'
    if changed == 'checkbox': after['SearchWords']['Candidates'][0]['Checked'] = True
    if changed == 'selection': after['Products']['Candidates'][0]['Selected'] = True
    result = compare_search_transition(before, after)
    assert result['search_content_changed'] == (changed in ('query', 'both', 'checkbox'))
    assert result['candidate_content_changed'] == (changed in ('candidate', 'both'))
    assert result['candidate_freshness_verified'] is False
    assert result['search_completion_verified'] is False
    assert result['can_write'] is False


@pytest.mark.parametrize('defect', ['source_mismatch', 'document', 'panel', 'skip', 'identity', 'completion', 'order', 'bool_position', 'checkbox'])
def test_unsafe_context_rejected(defect):
    before, after = context(), context(2)
    if defect == 'source_mismatch': after['SearchWords']['Source']['Title'] = 'Other'
    elif defect == 'document':
        for name in ('SearchWords', 'Products'): after[name]['Source']['DocumentNumber'] = '18'
    elif defect == 'panel': after['Products']['PanelHandle'] += 1
    elif defect == 'skip': after = context(3)
    elif defect == 'identity': after['Products']['IdentityVerified'] = True
    elif defect == 'completion': after['SearchCompletionVerified'] = True
    elif defect == 'order': after['Products']['Candidates'][0]['ObservedPosition'] = 2
    elif defect == 'bool_position': after['Products']['Source']['ObservedRowPosition'] = True
    else: after['SearchWords']['Candidates'][0]['Checked'] = 1
    with pytest.raises(PriceQueueError): compare_search_transition(before, after)


def test_empty_candidates_are_not_absence_proof():
    before, after = context(), context(2)
    after['Products']['Candidates'] = []
    result = compare_search_transition(before, after)
    assert result['after_candidate_count'] == 0
    assert result['products_processed'] is False
    assert result['candidate_freshness_verified'] is False
