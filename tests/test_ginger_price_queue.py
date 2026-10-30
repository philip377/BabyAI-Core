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
        'pending_attempt': None, 'navigation_exhausted': True,
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
