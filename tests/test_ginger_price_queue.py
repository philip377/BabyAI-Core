import json
from concurrent.futures import ThreadPoolExecutor
from threading import Barrier

import pytest
from babyai.ginger_price_queue import PriceQueueError, PriceVisitQueue, prices_from_report


def report():
    return {'process_id': 1, 'catalogue_changed': False, 'row_identity_verified': False,
            'prices': {'IdentityVerified': False, 'CanWrite': False, 'Rows': [
                {'Number': str(i), 'Supplier': 'Same supplier', 'Date': '2026-10-31',
                 'Included': i != 2} for i in range(1, 4)]}}


@pytest.fixture
def queue(tmp_path):
    queue = PriceVisitQueue(tmp_path / 'queue')
    queue.create(report())
    return queue


def document(number):
    return {'Number': number, 'Supplier': 'Same supplier', 'CanWrite': False}


def test_reopen_keeps_pending_and_order_without_claiming_business_completion(queue):
    attempt, first = queue.begin(report())
    assert first.number == '1'
    resumed = PriceVisitQueue(queue.directory)
    with pytest.raises(PriceQueueError, match='unresolved'):
        resumed.next(report())
    with pytest.raises(PriceQueueError, match='unresolved'):
        resumed.begin(report())
    resumed.observe(attempt, document('1'))
    assert resumed.next(report()).number == '3'
    attempt, second = resumed.begin(report())
    resumed.observe(attempt, document(second.number))
    assert resumed.next(report()) is None
    assert resumed.status() == {'planned_documents': 2, 'observed_documents': 2,
        'sampled_documents': 0, 'pending_document': None, 'pending_attempt': None, 'awaiting_read': False, 'requires_read': False, 'navigation_exhausted': True,
        'products_processed': False, 'can_write': False}
    with pytest.raises(PriceQueueError):
        resumed.begin(report())


@pytest.mark.parametrize('change', ['order', 'supplier', 'date', 'flag', 'insert', 'remove'])
def test_changed_list_cannot_resume(queue, change):
    fresh = report()
    rows = fresh['prices']['Rows']
    if change == 'order': rows.reverse()
    elif change == 'insert': rows.append(dict(rows[0], Number='4'))
    elif change == 'remove': rows.pop()
    else:
        rows[0][{'supplier': 'Supplier', 'date': 'Date', 'flag': 'Included'}[change]] = (
            False if change == 'flag' else 'changed')
    with pytest.raises(PriceQueueError, match='changed'):
        queue.begin(fresh)
    assert queue.status()['pending_attempt'] is None


@pytest.mark.parametrize('patch', [{'Number': '3'}, {'Supplier': 'other'}, {'CanWrite': True}])
def test_wrong_observation_leaves_pending(queue, patch):
    attempt, _ = queue.begin(report())
    with pytest.raises(PriceQueueError):
        queue.observe(attempt, dict(document('1'), **patch))
    assert queue.status()['pending_attempt'] == attempt


def test_old_attempt_and_repeated_observation_rejected(queue):
    attempt, _ = queue.begin(report())
    with pytest.raises(PriceQueueError): queue.observe('0' * 32, document('1'))
    queue.observe(attempt, document('1'))
    with pytest.raises(PriceQueueError): queue.observe(attempt, document('1'))


@pytest.mark.parametrize('damage', ['partial', 'gap', 'wrong_number', 'unknown', 'bad_plan'])
def test_crash_or_corruption_blocks_replay(queue, damage):
    if damage == 'partial':
        (queue.directory / '000001.json').write_text('{', encoding='utf-8')
    elif damage == 'gap':
        (queue.directory / '000002.json').write_text('{}', encoding='utf-8')
    elif damage == 'wrong_number':
        (queue.directory / '000001.json').write_text(json.dumps(
            {'kind': 'begin', 'attempt': 'a' * 32, 'number': '3'}), encoding='utf-8')
    elif damage == 'unknown':
        (queue.directory / 'unresolved.tmp').write_text('unknown', encoding='utf-8')
    else:
        (queue.directory / 'plan.json').write_text('null', encoding='utf-8')
    with pytest.raises(PriceQueueError): queue.next(report())


def test_two_controllers_cannot_dispatch_same_step(queue, monkeypatch):
    barrier = Barrier(2)
    original = PriceVisitQueue._write
    def competing(path, event):
        barrier.wait(timeout=5)
        return original(path, event)
    monkeypatch.setattr(PriceVisitQueue, '_write', staticmethod(competing))
    def begin():
        try: return PriceVisitQueue(queue.directory).begin(report())
        except PriceQueueError: return None
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(lambda _: begin(), range(2)))
    assert sum(result is not None for result in results) == 1
    assert queue.status()['pending_attempt'] is not None


def test_flags_and_duplicate_numbers_are_strict():
    for invalid in (1, 'true', None):
        fresh = report()
        fresh['prices']['Rows'][0]['Included'] = invalid
        with pytest.raises(PriceQueueError): prices_from_report(fresh)
    fresh = report()
    fresh['prices']['Rows'][2]['Number'] = '1'
    with pytest.raises(PriceQueueError): prices_from_report(fresh)


def test_existing_plan_cannot_be_reset(queue):
    with pytest.raises(PriceQueueError): queue.create(report())


def test_empty_plan_never_claims_products_processed(tmp_path):
    fresh = report()
    fresh['prices']['Rows'] = []
    queue = PriceVisitQueue(tmp_path / 'empty')
    queue.create(fresh)
    assert queue.next(fresh) is None
    assert queue.status()['products_processed'] is False


def test_cli_accepts_windows_utf8_bom_reports(tmp_path):
    import os
    import subprocess
    import sys
    from pathlib import Path
    source = tmp_path / 'windows-report.json'
    source.write_text(json.dumps(report()), encoding='utf-8-sig')
    env = dict(os.environ, PYTHONPATH=str(Path(__file__).resolve().parents[1] / 'src'))
    result = subprocess.run([sys.executable, '-m', 'babyai.ginger_price_queue', 'create',
        '--directory', str(tmp_path / 'plan'), '--report', str(source)],
        capture_output=True, text=True, env=env, timeout=10)
    assert result.returncode == 0, result.stderr
    status = json.loads(result.stdout)
    assert status['planned_documents'] == 2
    assert status['observed_documents'] == 0
    assert status['can_write'] is False


def test_fixed_checkout_entrypoint_works_outside_repository(tmp_path):
    import subprocess
    import sys
    from pathlib import Path
    entry = Path(__file__).resolve().parents[1] / 'scripts/windows/ginger-queue-cli.py'
    result = subprocess.run([sys.executable, str(entry), 'status', '--directory', str(tmp_path / 'missing')],
                            cwd=tmp_path, capture_output=True, text=True, timeout=10)
    assert result.returncode == 2
    assert 'Queue blocked:' in result.stderr
    assert 'No module named' not in result.stderr


def test_windows_controller_blocks_missing_confirmation_and_pending_attempt(queue, tmp_path):
    import os
    import subprocess
    import sys
    from pathlib import Path
    if sys.platform != 'win32':
        pytest.skip('Windows PowerShell controller')
    shell = Path(os.environ['WINDIR']) / 'System32/WindowsPowerShell/v1.0/powershell.exe'
    script = Path(__file__).resolve().parents[1] / 'scripts/windows/ginger-queue-step.ps1'
    command = [str(shell), '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(script),
               '-Operation', 'Step', '-QueueDirectory', str(queue.directory)]
    result = subprocess.run(command, capture_output=True, encoding='utf-8', cwd=tmp_path, timeout=15)
    assert result.returncode != 0
    assert 'Navigation confirmation required' in result.stderr
    assert queue.status()['pending_attempt'] is None
    attempt, _ = queue.begin(report())
    result = subprocess.run(command + ['-ConfirmNavigation'], capture_output=True,
                            encoding='utf-8', cwd=tmp_path, timeout=15)
    assert result.returncode != 0
    assert 'Previous navigation unresolved; no input permitted' in result.stderr
    assert queue.status()['pending_attempt'] == attempt
    assert queue.status()['observed_documents'] == 0


def sample(number='1'):
    return {'IdentityVerified': False, 'CanMatch': False, 'SearchCompletionVerified': False,
            'Source': {'DocumentNumber': number, 'Supplier': 'Same supplier', 'Title': 'Perfume',
                       'PriceText': '12.34', 'LinkedProductText': '<none>', 'ObservedRowPosition': 1,
                       'IdentityVerified': False, 'CanMatch': False},
            'Candidates': [{'ObservedPosition': 1, 'Title': 'Perfume tester',
                            'DescriptionText': 'Price: 0', 'Selected': False, 'IdentityVerified': False}]}


def test_required_read_survives_restart_and_blocks_next(tmp_path):
    queue = PriceVisitQueue(tmp_path / 'read-plan')
    queue.create(report(), require_read=True)
    attempt, _ = queue.begin(report())
    with pytest.raises(PriceQueueError): queue.record_read(attempt, sample())
    queue.observe(attempt, document('1'))
    resumed = PriceVisitQueue(queue.directory)
    assert resumed.status()['observed_documents'] == 1
    assert resumed.status()['sampled_documents'] == 0
    assert resumed.status()['awaiting_read'] is True
    with pytest.raises(PriceQueueError): resumed.begin(report())
    with pytest.raises(PriceQueueError): resumed.observe(attempt, document('1'))
    resumed.record_read(attempt, sample())
    assert resumed.status()['sampled_documents'] == 1
    assert resumed.status()['pending_attempt'] is None
    assert resumed.status()['products_processed'] is False
    assert resumed.next(report()).number == '3'
    with pytest.raises(PriceQueueError): resumed.record_read(attempt, sample())


@pytest.mark.parametrize('defect', ['wrong_document', 'permission', 'search_ready', 'missing', 'order', 'position', 'wrong_attempt'])
def test_invalid_read_keeps_queue_blocked(tmp_path, defect):
    queue = PriceVisitQueue(tmp_path / defect)
    queue.create(report(), require_read=True)
    attempt, _ = queue.begin(report())
    queue.observe(attempt, document('1'))
    reading = sample()
    token = attempt
    if defect == 'wrong_document': reading['Source']['DocumentNumber'] = '3'
    elif defect == 'permission': reading['CanMatch'] = True
    elif defect == 'search_ready': reading['SearchCompletionVerified'] = True
    elif defect == 'missing': del reading['Source']['PriceText']
    elif defect == 'order': reading['Candidates'][0]['ObservedPosition'] = 2
    elif defect == 'position': reading['Source']['ObservedRowPosition'] = True
    else: token = '0' * 32
    with pytest.raises(PriceQueueError): queue.record_read(token, reading)
    assert queue.status()['awaiting_read'] is True
    assert queue.status()['pending_attempt'] == attempt


def test_legacy_plan_is_not_silently_upgraded(queue):
    plan = queue.directory / 'plan.json'
    data = json.loads(plan.read_text())
    del data['require_read']
    plan.write_text(json.dumps(data))
    attempt, _ = queue.begin(report())
    queue.observe(attempt, document('1'))
    assert queue.status()['requires_read'] is False
    assert queue.status()['sampled_documents'] == 0
    assert queue.next(report()).number == '3'


def test_damaged_read_receipt_blocks_resume(tmp_path):
    queue = PriceVisitQueue(tmp_path / 'damaged-read')
    queue.create(report(), require_read=True)
    attempt, _ = queue.begin(report())
    queue.observe(attempt, document('1'))
    queue.record_read(attempt, sample())
    receipt = queue.directory / '000003.json'
    data = json.loads(receipt.read_text())
    data['sha256'] = 'incomplete'
    receipt.write_text(json.dumps(data))
    with pytest.raises(PriceQueueError): queue.next(report())


def test_empty_candidates_are_only_a_sample(tmp_path):
    queue = PriceVisitQueue(tmp_path / 'empty-reading')
    queue.create(report(), require_read=True)
    attempt, _ = queue.begin(report())
    queue.observe(attempt, document('1'))
    reading = sample()
    reading['Candidates'] = []
    queue.record_read(attempt, reading)
    assert queue.status()['sampled_documents'] == 1
    assert queue.status()['products_processed'] is False
    assert queue.status()['can_write'] is False


def test_windows_exhausted_controller_returns_without_ginger(tmp_path):
    import os
    import subprocess
    import sys
    from pathlib import Path
    if sys.platform != 'win32':
        pytest.skip('Windows PowerShell controller')
    queue = PriceVisitQueue(tmp_path / 'finished')
    fresh = report()
    fresh['prices']['Rows'] = []
    queue.create(fresh, require_read=True)
    shell = Path(os.environ['WINDIR']) / 'System32/WindowsPowerShell/v1.0/powershell.exe'
    script = Path(__file__).resolve().parents[1] / 'scripts/windows/ginger-queue-step.ps1'
    result = subprocess.run([str(shell), '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', str(script), '-Operation', 'Step', '-QueueDirectory', str(queue.directory),
        '-ConfirmNavigation'], capture_output=True, encoding='utf-8', cwd=tmp_path, timeout=15)
    assert result.returncode == 0, result.stderr
    status = json.loads(result.stdout)
    assert status['navigation_exhausted'] is True
    assert status['products_processed'] is False
    assert status['can_write'] is False
