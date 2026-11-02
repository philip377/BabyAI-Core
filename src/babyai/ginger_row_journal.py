"""Durable UI row-navigation receipts, never product identity or processing proof."""
import argparse
import hashlib
import json
from pathlib import Path
import re
from uuid import uuid4

from .ginger_price_queue import PriceQueueError, PriceVisitQueue


def snapshot(report, *, allow_ambiguous=False):
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
        if len(selected) != 1 and not allow_ambiguous:
            raise PriceQueueError('Exactly one selected row required')
        digest = hashlib.sha256(json.dumps({'process': report['process_id'], 'rows': clean},
                              sort_keys=True, ensure_ascii=False).encode('utf-8')).hexdigest()
        return digest, selected[0] if len(selected) == 1 else None, len(rows)
    except (KeyError, TypeError) as exc:
        raise PriceQueueError('Incomplete product report') from exc


class RowNavigationJournal:
    def __init__(self, directory):
        self.directory = Path(directory)

    def create(self, report, *, require_read=False):
        if type(require_read) is not bool:
            raise PriceQueueError('Invalid row reading policy')
        report = dict(report, require_read=require_read)
        snapshot(report)
        self.directory.mkdir(parents=True, exist_ok=True)
        if any(self.directory.iterdir()):
            raise PriceQueueError('New empty row journal required')
        PriceVisitQueue._write(self.directory / 'plan.json', report)

    def _state(self):
        plan = PriceVisitQueue._read(self.directory / 'plan.json')
        digest, selected, count = snapshot(plan)
        require_read = plan.get('require_read', False)
        if type(require_read) is not bool:
            raise PriceQueueError('Invalid row reading policy')
        names = sorted(p.name for p in self.directory.iterdir() if p.name != 'plan.json')
        if len(names) > 1500:
            raise PriceQueueError('Too many row events')
        pending, sampled, pending_kind = None, False, None
        for index, name in enumerate(names, 1):
            if name != f'{index:06d}.json':
                raise PriceQueueError('Missing or unknown row event')
            event = PriceVisitQueue._read(self.directory / name)
            if isinstance(event, dict) and event.get('kind') == 'sample':
                if (set(event) != {'kind', 'target', 'digest', 'sample_sha', 'candidate_count'}
                        or pending is not None or sampled
                        or type(event['target']) is not int or event['target'] != selected
                        or event['digest'] != digest or type(event['candidate_count']) is not int
                        or not 0 <= event['candidate_count'] <= 256
                        or not isinstance(event['sample_sha'], str)
                        or not re.fullmatch('[0-9a-f]{64}', event['sample_sha'])):
                    raise PriceQueueError('Invalid row sample receipt')
                sampled = True
                continue
            if (not isinstance(event, dict) or set(event) != {'kind', 'attempt', 'target', 'digest'}
                    or not isinstance(event['attempt'], str) or not re.fullmatch('[0-9a-f]{32}', event['attempt'])
                    or type(event['target']) is not int
                    or event['target'] != (selected if event['kind'] in {'recover', 'restored'} else selected + 1)
                    or event['target'] > count or event['digest'] != digest):
                raise PriceQueueError('Invalid row event')
            if event['kind'] == 'begin' and pending is None and (sampled or not require_read):
                pending, pending_kind = event['attempt'], 'begin'
            elif event['kind'] == 'recover' and pending is None:
                pending, pending_kind = event['attempt'], 'recover'
            elif event['kind'] == 'restored' and pending == event['attempt'] and pending_kind == 'recover':
                pending, sampled, pending_kind = None, False, None
            elif event['kind'] == 'observed' and pending == event['attempt'] and pending_kind == 'begin':
                selected += 1
                pending, sampled, pending_kind = None, False, None
            else:
                raise PriceQueueError('Invalid row transition')
        return digest, selected, count, pending, len(names), sampled, require_read, pending_kind

    def status(self):
        _, selected, count, pending, _, sampled, require_read, pending_kind = self._state()
        return {'pending_kind': pending_kind, 'selected_sampled': sampled, 'requires_read': require_read, 'selected_position': selected, 'observed_rows': count, 'pending_attempt': pending,
                'next_position': selected + 1 if selected < count else None,
                'can_write': False, 'products_processed': False, 'candidate_freshness_verified': False}

    def compare(self, report):
        """Classify a fresh observation without adopting selection or clearing intent."""
        digest, selected, count, pending, _, _, _, _ = self._state()
        fresh_digest, fresh_selected, fresh_count = snapshot(report, allow_ambiguous=True)
        same_table = (digest, count) == (fresh_digest, fresh_count)
        if not same_table:
            status = 'table_changed'
        elif pending is not None:
            status = 'pending_observation_required'
        elif fresh_selected is None:
            status = 'selection_ambiguous'
        elif fresh_selected != selected:
            status = 'selection_changed'
        else:
            status = 'checkpoint_observed'
        return {'status': status, 'same_table': same_table,
                'journal_position': selected, 'observed_position': fresh_selected,
                'pending_attempt': pending, 'input_sent': False,
                'journal_updated': False, 'can_write': False, 'can_match': False,
                'candidate_freshness_verified': False}

    def begin(self, report):
        digest, selected, count, pending, sequence, sampled, require_read, pending_kind = self._state()
        if pending is not None:
            raise PriceQueueError('Unresolved row navigation; no input permitted')
        if require_read and not sampled:
            raise PriceQueueError('Selected row must be sampled before navigation')
        if snapshot(report) != (digest, selected, count):
            raise PriceQueueError('Product table or selection changed')
        if selected == count:
            raise PriceQueueError('No next observed row')
        attempt = uuid4().hex
        PriceVisitQueue._write(self.directory / f'{sequence+1:06d}.json',
            {'kind': 'begin', 'attempt': attempt, 'target': selected + 1, 'digest': digest})
        return attempt

    def observe(self, attempt, report):
        digest, selected, count, pending, sequence, sampled, require_read, pending_kind = self._state()
        if pending is None or attempt != pending or pending_kind != 'begin':
            raise PriceQueueError('No matching pending row navigation')
        if snapshot(report) != (digest, selected + 1, count):
            raise PriceQueueError('Next row not verified; pending intent retained')
        PriceVisitQueue._write(self.directory / f'{sequence+1:06d}.json',
            {'kind': 'observed', 'attempt': attempt, 'target': selected + 1, 'digest': digest})


    def begin_recovery(self, report):
        digest, selected, count, pending, sequence, _, _, _ = self._state()
        if pending is not None:
            raise PriceQueueError('Unresolved navigation; recovery cannot repeat input')
        fresh_digest, _, fresh_count = snapshot(report, allow_ambiguous=True)
        if (fresh_digest, fresh_count) != (digest, count):
            raise PriceQueueError('Recovery table changed; no input permitted')
        attempt = uuid4().hex
        PriceVisitQueue._write(self.directory / f'{sequence+1:06d}.json',
            {'kind': 'recover', 'attempt': attempt, 'target': selected, 'digest': digest})
        return attempt, selected

    def observe_recovery(self, attempt, report):
        digest, selected, count, pending, sequence, _, _, pending_kind = self._state()
        if pending is None or pending != attempt or pending_kind != 'recover':
            raise PriceQueueError('No matching pending recovery')
        if snapshot(report) != (digest, selected, count):
            raise PriceQueueError('Checkpoint selection not verified')
        PriceVisitQueue._write(self.directory / f'{sequence+1:06d}.json',
            {'kind': 'restored', 'attempt': attempt, 'target': selected, 'digest': digest})

    def record_sample(self, report):
        digest, selected, count, pending, sequence, sampled, _, _ = self._state()
        if pending is not None or sampled:
            raise PriceQueueError('Pending navigation or already sampled row')
        try:
            if snapshot(report) != (digest, selected, count):
                raise PriceQueueError('Product table changed before sampling')
            reading = report['candidate_snapshot']
            source = reading['Source']
            expected = report['rows'][selected - 1]['Observation']
            keys = ('TableHandle', 'ObservedRowPosition', 'DocumentNumber', 'Supplier', 'Title', 'PriceText', 'LinkedProductText')
            if (any(source[k] != expected[k] or type(source[k]) is not type(expected[k]) for k in keys)
                    or source['IdentityVerified'] is not False or source['CanMatch'] is not False
                    or reading['IdentityVerified'] is not False or reading['CanMatch'] is not False
                    or reading['SearchCompletionVerified'] is not False):
                raise PriceQueueError('Sample source mismatch or unsupported freshness claim')
            candidates = reading['Candidates']
            if not isinstance(candidates, list) or len(candidates) > 256:
                raise PriceQueueError('Invalid candidate count')
            for position, candidate in enumerate(candidates, 1):
                if (type(candidate['ObservedPosition']) is not int or candidate['ObservedPosition'] != position
                        or candidate['IdentityVerified'] is not False or type(candidate['Selected']) is not bool
                        or not isinstance(candidate['Title'], str) or not candidate['Title'].strip()
                        or len(candidate['Title']) > 512 or not isinstance(candidate['DescriptionText'], str)
                        or len(candidate['DescriptionText']) > 2048):
                    raise PriceQueueError('Invalid candidate sample')
        except (KeyError, TypeError) as exc:
            raise PriceQueueError('Incomplete row sample') from exc
        sample_sha = hashlib.sha256(json.dumps(reading, sort_keys=True, ensure_ascii=False).encode('utf-8')).hexdigest()
        PriceVisitQueue._write(self.directory / f'{sequence+1:06d}.json',
            {'kind': 'sample', 'target': selected, 'digest': digest,
             'sample_sha': sample_sha, 'candidate_count': len(candidates)})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['create', 'status', 'compare', 'begin', 'observe', 'sample', 'recover', 'restored'])
    parser.add_argument('--directory', required=True, type=Path)
    parser.add_argument('--report', type=Path)
    parser.add_argument('--attempt')
    parser.add_argument('--require-read', action='store_true')
    args = parser.parse_args()
    journal = RowNavigationJournal(args.directory)
    try:
        report = None
        if args.action != 'status':
            if args.report is None:
                parser.error('--report required')
            report = PriceVisitQueue._read(args.report)
        if args.action == 'compare':
            print(json.dumps(journal.compare(report)))
            return
        if args.action == 'create':
            journal.create(report, require_read=args.require_read)
        elif args.action == 'begin':
            print(json.dumps({'attempt': journal.begin(report), 'can_write': False}))
            return
        elif args.action == 'recover':
            attempt, position = journal.begin_recovery(report)
            print(json.dumps({'attempt': attempt, 'position': position, 'can_write': False}))
            return
        elif args.action == 'restored':
            journal.observe_recovery(args.attempt, report)
        elif args.action == 'sample':
            journal.record_sample(report)
        elif args.action == 'observe':
            journal.observe(args.attempt, report)
        print(json.dumps(journal.status()))
    except PriceQueueError as exc:
        parser.exit(2, f'Row navigation blocked: {exc}\n')


if __name__ == '__main__':
    main()
