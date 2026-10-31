"""Local navigation receipts. A visited document is NOT a processed supplier price.

This module sends no UI input and grants no write permission. A controller must
record begin() before navigation, verify the opened UI, then record observe().
An unresolved attempt blocks subsequent navigation, including after restart.
"""
from dataclasses import asdict, dataclass
import json
import hashlib
import os
from pathlib import Path
import re
from uuid import uuid4


class PriceQueueError(RuntimeError):
    pass


def _text(value):
    if not isinstance(value, str) or not value.strip() or len(value) > 512:
        raise PriceQueueError("Missing or oversized price identity")
    return value


@dataclass(frozen=True)
class Price:
    number: str
    supplier: str
    date: str
    included: bool


def prices_from_report(report: dict) -> tuple[Price, ...]:
    try:
        if (report['row_identity_verified'] is not False
                or report['catalogue_changed'] is not False
                or type(report['process_id']) is not int or report['process_id'] <= 0):
            raise PriceQueueError("Invalid read-only price report")
        snapshot = report['prices']
        if snapshot['IdentityVerified'] is not False or snapshot['CanWrite'] is not False:
            raise PriceQueueError("Price observations cannot authorize writes")
        rows = snapshot['Rows']
        if not isinstance(rows, list) or len(rows) > 500:
            raise PriceQueueError("Invalid price row count")
        result = []
        seen = set()
        for row in rows:
            if type(row['Included']) is not bool:
                raise PriceQueueError("Unknown inclusion flag")
            price = Price(_text(row['Number']), _text(row['Supplier']),
                          _text(row['Date']), row['Included'])
            if price.number in seen:
                raise PriceQueueError("Duplicate document number")
            seen.add(price.number)
            result.append(price)
        return tuple(result)
    except (KeyError, TypeError) as exc:
        raise PriceQueueError("Incomplete price report") from exc


class PriceVisitQueue:
    """Append-only, exclusive receipts; a partial write conservatively blocks replay.

    One directory represents one explicitly started plan. No auto-reset, skipping,
    merging of changed plans, or implicit completion of business work is provided.
    """
    def __init__(self, directory: Path):
        self.directory = Path(directory)

    @staticmethod
    def _write(path, record):
        try:
            with path.open('x', encoding='utf-8') as stream:
                json.dump(record, stream, ensure_ascii=False, sort_keys=True)
                stream.flush()
                os.fsync(stream.fileno())
        except FileExistsError as exc:
            raise PriceQueueError("Queue changed concurrently; reread required") from exc

    @staticmethod
    def _read(path):
        try:
            with path.open('r', encoding='utf-8-sig') as stream:
                text = stream.read(1_000_001)
            if len(text) > 1_000_000:
                raise PriceQueueError("Queue record exceeds limit")
            return json.loads(text)
        except (OSError, UnicodeError, ValueError) as exc:
            raise PriceQueueError("Queue record missing or damaged; manual review required") from exc

    def create(self, report: dict, *, require_read: bool = False):
        if type(require_read) is not bool:
            raise PriceQueueError('Invalid reading policy')
        rows = prices_from_report(report)
        self.directory.mkdir(parents=True, exist_ok=True)
        if any(self.directory.iterdir()):
            raise PriceQueueError("A new empty plan directory is required")
        self._write(self.directory / 'plan.json', {
            'version': 1, 'read_only': True, 'require_read': require_read, 'rows': [asdict(p) for p in rows]})

    def _state(self):
        record = self._read(self.directory / 'plan.json')
        try:
            if type(record['version']) is not int or record['version'] != 1 or record['read_only'] is not True:
                raise PriceQueueError("Unsupported queue plan")
            raw = record['rows']
            # Apply the same strict validation to persisted tuples as to fresh UI observations.
            rows = prices_from_report({'row_identity_verified': False, 'catalogue_changed': False,
                'process_id': 1, 'prices': {'IdentityVerified': False, 'CanWrite': False,
                'Rows': [{'Number': p['number'], 'Supplier': p['supplier'], 'Date': p['date'],
                          'Included': p['included']} for p in raw]}})
        except (KeyError, TypeError) as exc:
            raise PriceQueueError("Damaged queue plan") from exc
        included = tuple(p for p in rows if p.included)
        names = sorted(p.name for p in self.directory.iterdir() if p.name != 'plan.json')
        if len(names) > 1500:
            raise PriceQueueError("Too many queue events")
        require_read = record.get('require_read', False)
        if type(require_read) is not bool:
            raise PriceQueueError('Invalid reading policy')
        visited, pending, opened = 0, None, False
        for index, name in enumerate(names, 1):
            if name != f'{index:06d}.json':
                raise PriceQueueError("Unknown or missing queue event")
            event = self._read(self.directory / name)
            fields = {'kind', 'attempt', 'number'}
            if isinstance(event, dict) and event.get('kind') == 'read':
                fields |= {'candidate_count', 'source_position', 'sha256'}
            if not isinstance(event, dict) or set(event) != fields:
                raise PriceQueueError("Invalid queue event")
            if (visited >= len(included) or event['number'] != included[visited].number
                    or not isinstance(event['attempt'], str)
                    or re.fullmatch('[0-9a-f]{32}', event['attempt']) is None):
                raise PriceQueueError("Queue event does not match plan")
            if event['kind'] == 'begin' and pending is None:
                pending = event['attempt']
            elif event['kind'] == 'observed' and pending == event['attempt'] and not opened:
                if require_read:
                    opened = True
                else:
                    visited += 1
                    pending = None
            elif event['kind'] == 'read' and require_read and opened and pending == event['attempt']:
                if (type(event['candidate_count']) is not int or not 0 <= event['candidate_count'] <= 256
                        or type(event['source_position']) is not int or not 1 <= event['source_position'] <= 500
                        or not isinstance(event['sha256'], str)
                        or re.fullmatch('[0-9a-f]{64}', event['sha256']) is None):
                    raise PriceQueueError('Invalid read receipt')
                visited += 1
                pending, opened = None, False
            else:
                raise PriceQueueError("Invalid queue transition")
        return rows, included, visited, pending, len(names), opened, require_read

    def status(self):
        _, included, visited, pending, _, opened, require_read = self._state()
        return {'planned_documents': len(included), 'observed_documents': visited + int(opened),
                'sampled_documents': visited if require_read else 0,
                'pending_document': asdict(included[visited]) if pending else None,
                'pending_attempt': pending, 'awaiting_read': opened, 'requires_read': require_read, 'navigation_exhausted': visited == len(included),
                'products_processed': False, 'can_write': False}

    def next(self, fresh_report: dict):
        rows, included, visited, pending, _, _, _ = self._state()
        if rows != prices_from_report(fresh_report):
            raise PriceQueueError("Price list changed; plan must be reviewed")
        if pending is not None:
            raise PriceQueueError("Previous navigation outcome unresolved; no retry")
        return included[visited] if visited < len(included) else None

    def begin(self, fresh_report: dict):
        # Use one replay for both the planned item and event sequence, so a competing
        # writer cannot cause this instance to append after an unseen transition.
        rows, included, visited, pending, sequence, _, _ = self._state()
        if rows != prices_from_report(fresh_report):
            raise PriceQueueError("Price list changed; plan must be reviewed")
        if pending is not None:
            raise PriceQueueError("Previous navigation outcome unresolved; no retry")
        if visited == len(included):
            raise PriceQueueError("No further planned document")
        price = included[visited]
        attempt = uuid4().hex
        self._write(self.directory / f'{sequence+1:06d}.json',
                    {'kind': 'begin', 'attempt': attempt, 'number': price.number})
        return attempt, price

    def observe(self, attempt: str, document: dict):
        _, included, visited, pending, sequence, opened, _ = self._state()
        if pending is None or attempt != pending or opened:
            raise PriceQueueError("No matching pending navigation")
        price = included[visited]
        if (not isinstance(document, dict) or document.get('CanWrite') is not False
                or document.get('Number') != price.number or document.get('Supplier') != price.supplier):
            raise PriceQueueError("Opened document does not match pending navigation")
        self._write(self.directory / f'{sequence+1:06d}.json',
                    {'kind': 'observed', 'attempt': attempt, 'number': price.number})


    def record_read(self, attempt: str, snapshot: dict):
        _, included, visited, pending, sequence, opened, require_read = self._state()
        if not require_read or not opened or pending != attempt:
            raise PriceQueueError('No opened document awaiting this reading')
        price = included[visited]
        try:
            source = snapshot['Source']
            if (snapshot['IdentityVerified'] is not False or snapshot['CanMatch'] is not False
                    or snapshot['SearchCompletionVerified'] is not False
                    or source['IdentityVerified'] is not False or source['CanMatch'] is not False
                    or source['DocumentNumber'] != price.number or source['Supplier'] != price.supplier):
                raise PriceQueueError('Reading does not match the opened document')
            for key in ('Title', 'PriceText', 'LinkedProductText'):
                _text(source[key])
            position = source['ObservedRowPosition']
            candidates = snapshot['Candidates']
            if (type(position) is not int or not 1 <= position <= 500
                    or not isinstance(candidates, list) or len(candidates) > 256):
                raise PriceQueueError('Invalid reading size or source position')
            for index, candidate in enumerate(candidates, 1):
                _text(candidate['Title'])
                if (type(candidate['ObservedPosition']) is not int or candidate['ObservedPosition'] != index
                        or candidate['IdentityVerified'] is not False or type(candidate['Selected']) is not bool
                        or not isinstance(candidate['DescriptionText'], str)
                        or len(candidate['DescriptionText']) > 2048):
                    raise PriceQueueError('Invalid candidate observation')
        except (KeyError, TypeError) as exc:
            raise PriceQueueError('Incomplete reading') from exc
        digest = hashlib.sha256(json.dumps(snapshot, sort_keys=True, ensure_ascii=False).encode('utf-8')).hexdigest()
        self._write(self.directory / f'{sequence+1:06d}.json', {
            'kind': 'read', 'attempt': attempt, 'number': price.number,
            'candidate_count': len(candidates), 'source_position': position, 'sha256': digest})


def main():
    """Explicit local diagnostics CLI; never launches Ginger or sends UI input."""
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['create', 'status', 'next', 'begin', 'observe', 'read'])
    parser.add_argument('--directory', required=True, type=Path)
    parser.add_argument('--report', type=Path)
    parser.add_argument('--attempt')
    parser.add_argument('--require-read', action='store_true')
    args = parser.parse_args()
    queue = PriceVisitQueue(args.directory)
    try:
        report = None
        if args.action in {'create', 'next', 'begin', 'observe', 'read'}:
            if args.report is None:
                parser.error('--report is required for this action')
            report = queue._read(args.report)
        if args.action == 'create':
            queue.create(report, require_read=args.require_read)
            result = queue.status()
        elif args.action == 'status':
            result = queue.status()
        elif args.action == 'next':
            price = queue.next(report)
            result = {'next_document': asdict(price) if price else None, 'can_write': False}
        elif args.action == 'begin':
            attempt, price = queue.begin(report)
            result = {'attempt': attempt, 'document': asdict(price), 'can_write': False}
        else:
            if not args.attempt:
                parser.error('--attempt is required for observe')
            if args.action == 'read':
                queue.record_read(args.attempt, report)
            else:
                queue.observe(args.attempt, report)
            result = queue.status()
        print(json.dumps(result, ensure_ascii=False))
    except PriceQueueError as exc:
        parser.exit(2, f'Queue blocked: {exc}\n')


if __name__ == '__main__':
    main()
