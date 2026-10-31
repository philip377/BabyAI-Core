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


def test_windows_row_controller_guards_before_ui(tmp_path):
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
               '-Operation', 'Step', '-QueueDirectory', str(journal.directory)]
    result = subprocess.run(command, capture_output=True, encoding='utf-8', cwd=tmp_path, timeout=15)
    assert result.returncode != 0
    assert 'Navigation confirmation required' in result.stderr
    token = journal.begin(report())
    result = subprocess.run(command + ['-ConfirmNavigation'], capture_output=True, encoding='utf-8', cwd=tmp_path, timeout=15)
    assert result.returncode != 0
    assert 'Unresolved row navigation; no input permitted' in result.stderr
    assert journal.status()['pending_attempt'] == token
