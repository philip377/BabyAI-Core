import json
import pytest
from babyai.ginger_row_journal import RowNavigationJournal, snapshot
from babyai.ginger_price_queue import PriceQueueError


def report(selected=1):
    return {'process_id': 42, 'can_write': False, 'rows': [
        {'Selected': i == selected, 'Observation': {'TableHandle': 123, 'ObservedRowPosition': i,
         'DocumentNumber': '17', 'Supplier': 'Supplier', 'Title': 'Same name', 'PriceText': '10',
         'LinkedProductText': '<none>', 'IdentityVerified': False, 'CanMatch': False}} for i in range(1, 4)]}


def test_restart_unknown_result_cannot_retry(tmp_path):
    journal = RowNavigationJournal(tmp_path)
    journal.create(report())
    token = journal.begin(report())
    resumed = RowNavigationJournal(tmp_path)
    for fresh in (report(), report(2)):
        with pytest.raises(PriceQueueError): resumed.begin(fresh)
    with pytest.raises(PriceQueueError): resumed.observe(token, report())
    assert resumed.status()['pending_attempt'] == token
    resumed.observe(token, report(2))
    assert resumed.status()['selected_position'] == 2
    assert resumed.status()['candidate_freshness_verified'] is False
    assert resumed.status()['products_processed'] is False
    with pytest.raises(PriceQueueError): resumed.observe(token, report(2))
    token = resumed.begin(report(2))
    resumed.observe(token, report(3))
    assert resumed.status()['next_position'] is None
    with pytest.raises(PriceQueueError): resumed.begin(report(3))


@pytest.mark.parametrize('defect', ['title', 'price', 'supplier', 'document', 'handle', 'process', 'selection', 'count'])
def test_stale_table_blocks_begin(tmp_path, defect):
    journal = RowNavigationJournal(tmp_path)
    journal.create(report())
    fresh = report()
    key = {'title': 'Title', 'price': 'PriceText', 'supplier': 'Supplier', 'document': 'DocumentNumber', 'handle': 'TableHandle'}
    if defect in key: fresh['rows'][2]['Observation'][key[defect]] = 999 if defect == 'handle' else 'Changed'
    elif defect == 'process': fresh['process_id'] += 1
    elif defect == 'selection': fresh = report(2)
    else: fresh['rows'].pop()
    with pytest.raises(PriceQueueError): journal.begin(fresh)
    assert journal.status()['pending_attempt'] is None


@pytest.mark.parametrize('defect', ['wrong_token', 'wrong_row', 'price', 'permission'])
def test_invalid_observation_keeps_pending(tmp_path, defect):
    journal = RowNavigationJournal(tmp_path)
    journal.create(report())
    token = journal.begin(report())
    fresh = report(2)
    supplied = token
    if defect == 'wrong_token': supplied = '0' * 32
    elif defect == 'wrong_row': fresh = report(3)
    elif defect == 'price': fresh['rows'][0]['Observation']['PriceText'] = '11'
    else: fresh['can_write'] = True
    with pytest.raises(PriceQueueError): journal.observe(supplied, fresh)
    assert journal.status()['pending_attempt'] == token


@pytest.mark.parametrize('defect', ['partial', 'gap', 'unknown', 'tampered'])
def test_damaged_receipts_fail_closed(tmp_path, defect):
    journal = RowNavigationJournal(tmp_path)
    journal.create(report())
    journal.begin(report())
    event = tmp_path / '000001.json'
    if defect == 'partial': event.write_text('{')
    elif defect == 'gap': event.rename(tmp_path / '000002.json')
    elif defect == 'unknown': (tmp_path / 'unknown').write_text('x')
    else:
        data = json.loads(event.read_text()); data['target'] = 3
        event.write_text(json.dumps(data))
    with pytest.raises(PriceQueueError): journal.status()


def test_two_writers_exclusive_intent(tmp_path, monkeypatch):
    journal = RowNavigationJournal(tmp_path)
    journal.create(report())
    stale = journal._state()
    first = journal.begin(report())
    monkeypatch.setattr(journal, '_state', lambda: stale)
    with pytest.raises(PriceQueueError): journal.begin(report())
    assert RowNavigationJournal(tmp_path).status()['pending_attempt'] == first


@pytest.mark.parametrize('defect', ['bool_position', 'multiple', 'none', 'blank', 'oversize', 'identity'])
def test_invalid_report(defect):
    fresh = report()
    if defect == 'bool_position': fresh['rows'][0]['Observation']['ObservedRowPosition'] = True
    elif defect == 'multiple': fresh['rows'][1]['Selected'] = True
    elif defect == 'none': fresh['rows'][0]['Selected'] = False
    elif defect == 'blank': fresh['rows'][0]['Observation']['Title'] = ''
    elif defect == 'oversize': fresh['rows'] *= 200
    else: fresh['rows'][0]['Observation']['IdentityVerified'] = True
    with pytest.raises(PriceQueueError): snapshot(fresh)


@pytest.mark.parametrize('operation', ['Step', 'Recover'])
def test_windows_row_controller_guards_before_ui(tmp_path, operation):
    import os
    import subprocess
    import sys
    from pathlib import Path
    if sys.platform != 'win32':
        pytest.skip('Windows PowerShell controller')
    journal = RowNavigationJournal(tmp_path / 'plan')
    journal.create(report())
    shell = Path(os.environ['WINDIR']) / 'System32/WindowsPowerShell/v1.0/powershell.exe'
    script = Path(__file__).resolve().parents[1] / 'scripts/windows/ginger-row-step.ps1'
    command = [str(shell), '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(script),
               '-Operation', operation, '-QueueDirectory', str(journal.directory)]
    result = subprocess.run(command, capture_output=True, encoding='utf-8', cwd=tmp_path, timeout=15)
    assert result.returncode != 0
    assert 'Navigation confirmation required' in result.stderr
    token = journal.begin(report())
    result = subprocess.run(command + ['-ConfirmNavigation'], capture_output=True, encoding='utf-8', cwd=tmp_path, timeout=15)
    assert result.returncode != 0
    assert 'Unresolved row navigation; no input permitted' in result.stderr
    assert journal.status()['pending_attempt'] == token


def sample_report(selected=1):
    import copy
    fresh = report(selected)
    fresh['candidate_snapshot'] = {
        'Source': copy.deepcopy(fresh['rows'][selected - 1]['Observation']),
        'IdentityVerified': False, 'CanMatch': False, 'SearchCompletionVerified': False,
        'Candidates': [{'ObservedPosition': 1, 'Title': 'Candidate', 'DescriptionText': 'Price: 10',
                        'Selected': False, 'IdentityVerified': False}]}
    return fresh


def test_required_sample_survives_restart(tmp_path):
    journal = RowNavigationJournal(tmp_path)
    journal.create(report(), require_read=True)
    with pytest.raises(PriceQueueError): journal.begin(report())
    journal.record_sample(sample_report())
    resumed = RowNavigationJournal(tmp_path)
    assert resumed.status()['selected_sampled'] is True
    assert resumed.status()['candidate_freshness_verified'] is False
    with pytest.raises(PriceQueueError): resumed.record_sample(sample_report())
    token = resumed.begin(report())
    with pytest.raises(PriceQueueError): resumed.record_sample(sample_report(2))
    resumed.observe(token, report(2))
    assert resumed.status()['selected_sampled'] is False
    with pytest.raises(PriceQueueError): resumed.begin(report(2))
    resumed.record_sample(sample_report(2))
    assert resumed.status()['selected_sampled'] is True


@pytest.mark.parametrize('defect', ['source', 'price', 'bool_position', 'freshness', 'permission', 'order', 'title', 'count', 'missing'])
def test_invalid_sample_cannot_unlock_navigation(tmp_path, defect):
    journal = RowNavigationJournal(tmp_path)
    journal.create(report(), require_read=True)
    fresh = sample_report()
    reading = fresh['candidate_snapshot']
    if defect == 'source': reading['Source']['ObservedRowPosition'] = 2
    elif defect == 'price': reading['Source']['PriceText'] = '11'
    elif defect == 'bool_position': reading['Source']['ObservedRowPosition'] = True
    elif defect == 'freshness': reading['SearchCompletionVerified'] = True
    elif defect == 'permission': reading['CanMatch'] = True
    elif defect == 'order': reading['Candidates'][0]['ObservedPosition'] = 2
    elif defect == 'title': reading['Candidates'][0]['Title'] = ''
    elif defect == 'count': reading['Candidates'] *= 257
    else: del reading['Source']
    with pytest.raises(PriceQueueError): journal.record_sample(fresh)
    assert journal.status()['selected_sampled'] is False
    with pytest.raises(PriceQueueError): journal.begin(report())


def test_legacy_policy_not_upgraded_and_empty_sample_is_not_completion(tmp_path):
    journal = RowNavigationJournal(tmp_path)
    journal.create(report())
    plan = tmp_path / 'plan.json'
    data = json.loads(plan.read_text()); del data['require_read']
    plan.write_text(json.dumps(data))
    assert journal.status()['requires_read'] is False
    fresh = sample_report(); fresh['candidate_snapshot']['Candidates'] = []
    journal.record_sample(fresh)
    assert journal.status()['products_processed'] is False
    assert journal.status()['candidate_freshness_verified'] is False


def test_corrupt_sample_receipt_blocks_replay(tmp_path):
    journal = RowNavigationJournal(tmp_path)
    journal.create(report(), require_read=True)
    journal.record_sample(sample_report())
    event = tmp_path / '000001.json'
    data = json.loads(event.read_text()); data['sample_sha'] = 'partial'
    event.write_text(json.dumps(data))
    with pytest.raises(PriceQueueError): journal.status()


def multi_report():
    fresh = report(2)
    fresh['rows'][2]['Selected'] = True
    return fresh


def test_recovery_survives_restart_without_repeating_input(tmp_path):
    journal = RowNavigationJournal(tmp_path)
    journal.create(report(), require_read=True)
    journal.record_sample(sample_report())
    token, target = journal.begin_recovery(multi_report())
    assert target == 1
    resumed = RowNavigationJournal(tmp_path)
    assert resumed.status()['pending_kind'] == 'recover'
    with pytest.raises(PriceQueueError): resumed.begin_recovery(multi_report())
    with pytest.raises(PriceQueueError): resumed.begin(report())
    with pytest.raises(PriceQueueError): resumed.observe(token, report(2))
    with pytest.raises(PriceQueueError): resumed.observe_recovery(token, multi_report())
    with pytest.raises(PriceQueueError): resumed.observe_recovery(token, report(2))
    resumed.observe_recovery(token, report())
    assert resumed.status()['pending_attempt'] is None
    assert resumed.status()['selected_position'] == 1
    assert resumed.status()['selected_sampled'] is False
    with pytest.raises(PriceQueueError): resumed.begin(report())
    resumed.record_sample(sample_report())
    assert resumed.status()['candidate_freshness_verified'] is False


@pytest.mark.parametrize('defect', ['title', 'count', 'process', 'order', 'permission'])
def test_recovery_requires_identical_table(tmp_path, defect):
    journal = RowNavigationJournal(tmp_path)
    journal.create(report())
    fresh = multi_report()
    if defect == 'title': fresh['rows'][0]['Observation']['Title'] = 'Changed'
    elif defect == 'count': fresh['rows'].pop()
    elif defect == 'process': fresh['process_id'] += 1
    elif defect == 'order': fresh['rows'].reverse()
    else: fresh['can_write'] = True
    with pytest.raises(PriceQueueError): journal.begin_recovery(fresh)
    assert journal.status()['pending_attempt'] is None


def test_pending_forward_navigation_cannot_be_recovered(tmp_path):
    journal = RowNavigationJournal(tmp_path)
    journal.create(report())
    token = journal.begin(report())
    with pytest.raises(PriceQueueError): journal.begin_recovery(multi_report())
    with pytest.raises(PriceQueueError): journal.observe_recovery(token, report())
    assert journal.status()['pending_kind'] == 'begin'


@pytest.mark.parametrize('case,expected', [
    ('same', 'checkpoint_observed'), ('selection', 'selection_changed'),
    ('ambiguous', 'selection_ambiguous'), ('process', 'table_changed'),
    ('price', 'table_changed'), ('pending', 'pending_observation_required')])
def test_compare_never_adopts_new_selection_or_clears_intent(tmp_path, case, expected):
    journal = RowNavigationJournal(tmp_path)
    journal.create(report())
    fresh = report()
    if case == 'selection': fresh = report(2)
    elif case == 'ambiguous': fresh['rows'][1]['Selected'] = True
    elif case == 'process': fresh['process_id'] += 1
    elif case == 'price': fresh['rows'][0]['Observation']['PriceText'] = '999'
    elif case == 'pending':
        journal.begin(report())
        fresh = report(2)
    original = {p.name: p.read_bytes() for p in tmp_path.iterdir()}
    result = journal.compare(fresh)
    assert result['status'] == expected
    assert result['journal_position'] == 1
    assert all(result[key] is False for key in ('input_sent', 'journal_updated', 'can_write', 'can_match', 'candidate_freshness_verified'))
    assert original == {p.name: p.read_bytes() for p in tmp_path.iterdir()}
    assert journal.status()['selected_position'] == 1
    if case == 'pending': assert journal.status()['pending_attempt'] is not None
