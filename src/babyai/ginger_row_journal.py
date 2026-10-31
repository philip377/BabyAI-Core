"""Durable UI row-navigation receipts, never product identity or processing proof."""
import argparse
import hashlib
import json
from pathlib import Path
import re
from uuid import uuid4

from .ginger_price_queue import PriceQueueError, PriceVisitQueue


def snapshot(report):
    """Validate a complete bounded observation from the trusted local driver."""
    try:
        rows = report['rows']
        if (report['can_write'] is not False or type(report['process_id']) is not int
                or report['process_id'] <= 0 or not isinstance(rows, list) or not 1 <= len(rows) <= 500):
            raise PriceQueueError('Invalid product table report')
        clean, selected, document = [], [], None
        for position, row in enumerate(rows, 1):
            item = row['Observation']
            if (item['IdentityVerified'] is not False or item['CanMatch'] is not False
                    or type(row['Selected']) is not bool
                    or type(item['ObservedRowPosition']) is not int or item['ObservedRowPosition'] != position
                    or type(item['TableHandle']) is not int or item['TableHandle'] <= 0):
                raise PriceQueueError('Invalid product observation')
            fields = {key: item[key] for key in ('DocumentNumber', 'Supplier', 'Title', 'PriceText', 'LinkedProductText')}
            if any(not isinstance(value, str) or not value.strip() or len(value) > 512 for value in fields.values()):
                raise PriceQueueError('Missing product fields')
            identity = (fields['DocumentNumber'], fields['Supplier'], item['TableHandle'])
            if document is not None and identity != document:
                raise PriceQueueError('Mixed product documents')
            document = identity
            clean.append(dict(fields, TableHandle=item['TableHandle'], ObservedRowPosition=position))
            if row['Selected']:
                selected.append(position)
        if len(selected) != 1:
            raise PriceQueueError('Exactly one selected row required')
        digest = hashlib.sha256(json.dumps({'process': report['process_id'], 'rows': clean},
                              sort_keys=True, ensure_ascii=False).encode('utf-8')).hexdigest()
        return digest, selected[0], len(rows)
    except (KeyError, TypeError) as exc:
        raise PriceQueueError('Incomplete product report') from exc


class RowNavigationJournal:
    def __init__(self, directory):
        self.directory = Path(directory)

    def create(self, report):
        snapshot(report)
        self.directory.mkdir(parents=True, exist_ok=True)
        if any(self.directory.iterdir()):
            raise PriceQueueError('New empty row journal required')
        PriceVisitQueue._write(self.directory / 'plan.json', report)

    def _state(self):
        digest, selected, count = snapshot(PriceVisitQueue._read(self.directory / 'plan.json'))
        names = sorted(p.name for p in self.directory.iterdir() if p.name != 'plan.json')
        if len(names) > 1000:
            raise PriceQueueError('Too many row events')
        pending = None
        for index, name in enumerate(names, 1):
            if name != f'{index:06d}.json':
                raise PriceQueueError('Missing or unknown row event')
            event = PriceVisitQueue._read(self.directory / name)
            if (not isinstance(event, dict) or set(event) != {'kind', 'attempt', 'target', 'digest'}
                    or not isinstance(event['attempt'], str) or not re.fullmatch('[0-9a-f]{32}', event['attempt'])
                    or type(event['target']) is not int or event['target'] != selected + 1
                    or event['target'] > count or event['digest'] != digest):
                raise PriceQueueError('Invalid row event')
            if event['kind'] == 'begin' and pending is None:
                pending = event['attempt']
            elif event['kind'] == 'observed' and pending == event['attempt']:
                selected += 1
                pending = None
            else:
                raise PriceQueueError('Invalid row transition')
        return digest, selected, count, pending, len(names)

    def status(self):
        _, selected, count, pending, _ = self._state()
        return {'selected_position': selected, 'observed_rows': count, 'pending_attempt': pending,
                'next_position': selected + 1 if selected < count else None,
                'can_write': False, 'products_processed': False, 'candidate_freshness_verified': False}

    def begin(self, report):
        digest, selected, count, pending, sequence = self._state()
        if pending is not None:
            raise PriceQueueError('Unresolved row navigation; no input permitted')
        if snapshot(report) != (digest, selected, count):
            raise PriceQueueError('Product table or selection changed')
        if selected == count:
            raise PriceQueueError('No next observed row')
        attempt = uuid4().hex
        PriceVisitQueue._write(self.directory / f'{sequence+1:06d}.json',
            {'kind': 'begin', 'attempt': attempt, 'target': selected + 1, 'digest': digest})
        return attempt

    def observe(self, attempt, report):
        digest, selected, count, pending, sequence = self._state()
        if pending is None or attempt != pending:
            raise PriceQueueError('No matching pending row navigation')
        if snapshot(report) != (digest, selected + 1, count):
            raise PriceQueueError('Next row not verified; pending intent retained')
        PriceVisitQueue._write(self.directory / f'{sequence+1:06d}.json',
            {'kind': 'observed', 'attempt': attempt, 'target': selected + 1, 'digest': digest})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['create', 'status', 'begin', 'observe'])
    parser.add_argument('--directory', required=True, type=Path)
    parser.add_argument('--report', type=Path)
    parser.add_argument('--attempt')
    args = parser.parse_args()
    journal = RowNavigationJournal(args.directory)
    try:
        report = None
        if args.action != 'status':
            if args.report is None:
                parser.error('--report required')
            report = PriceVisitQueue._read(args.report)
        if args.action == 'create':
            journal.create(report)
        elif args.action == 'begin':
            print(json.dumps({'attempt': journal.begin(report), 'can_write': False}))
            return
        elif args.action == 'observe':
            journal.observe(args.attempt, report)
        print(json.dumps(journal.status()))
    except PriceQueueError as exc:
        parser.exit(2, f'Row navigation blocked: {exc}\n')


if __name__ == '__main__':
    main()
