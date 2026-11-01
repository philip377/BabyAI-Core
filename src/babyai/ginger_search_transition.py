"""Compare local search observations; changes are not proof of search completion."""
import argparse
import json
from pathlib import Path
from .ginger_price_queue import PriceQueueError, PriceVisitQueue


def _context(value):
    try:
        if value['CanMatch'] is not False or value['SearchCompletionVerified'] is not False:
            raise PriceQueueError('Unsupported authorization or completion claim')
        sources, panels, contents = [], [], []
        for name in ('SearchWords', 'Products'):
            panel = value[name]
            source = panel['Source']
            if (panel['CanMatch'] is not False or panel['IdentityVerified'] is not False
                    or panel['SearchCompletionVerified'] is not False or source['CanMatch'] is not False
                    or source['IdentityVerified'] is not False
                    or type(panel['PanelHandle']) is not int or panel['PanelHandle'] <= 0
                    or type(source['TableHandle']) is not int or source['TableHandle'] <= 0
                    or type(source['ObservedRowPosition']) is not int or not 1 <= source['ObservedRowPosition'] <= 500):
                raise PriceQueueError('Invalid read-only search source')
            fields = tuple(source[k] for k in ('DocumentNumber', 'Supplier', 'Title', 'PriceText', 'LinkedProductText'))
            if any(not isinstance(v, str) or not v.strip() or len(v) > 512 for v in fields):
                raise PriceQueueError('Missing source text')
            sources.append((source['TableHandle'], source['ObservedRowPosition'], *fields))
            panels.append(panel['PanelHandle'])
            rows = panel['Candidates']
            if not isinstance(rows, list) or len(rows) > 256:
                raise PriceQueueError('Invalid search row count')
            parsed = []
            for position, row in enumerate(rows, 1):
                if (type(row['ObservedPosition']) is not int or row['ObservedPosition'] != position
                        or row['IdentityVerified'] is not False or type(row['Selected']) is not bool
                        or not isinstance(row['Title'], str) or not row['Title'].strip() or len(row['Title']) > 512
                        or not isinstance(row['DescriptionText'], str) or len(row['DescriptionText']) > 2048
                        or (row['Checked'] is not None and type(row['Checked']) is not bool)):
                    raise PriceQueueError('Invalid search row')
                # Selection/focus alone does not mean content changed.
                parsed.append((row['Title'], row['DescriptionText'], row['Checked']))
            contents.append(tuple(parsed))
        if sources[0] != sources[1] or panels[0] == panels[1]:
            raise PriceQueueError('Conflicting search context')
        return sources[0], tuple(panels), contents
    except (KeyError, TypeError) as exc:
        raise PriceQueueError('Incomplete search context') from exc


def compare_search_transition(before, after):
    old_source, old_panels, old_contents = _context(before)
    new_source, new_panels, new_contents = _context(after)
    if (old_source[0] != new_source[0] or old_source[2:4] != new_source[2:4]
            or new_source[1] != old_source[1] + 1 or old_panels != new_panels):
        raise PriceQueueError('Expected adjacent rows in the same document and panels')
    return {'from_position': old_source[1], 'to_position': new_source[1],
            'source_text_changed': old_source[4:] != new_source[4:],
            'search_content_changed': old_contents[0] != new_contents[0],
            'candidate_content_changed': old_contents[1] != new_contents[1],
            'before_candidate_count': len(old_contents[1]), 'after_candidate_count': len(new_contents[1]),
            'candidate_freshness_verified': False, 'search_completion_verified': False,
            'can_write': False, 'products_processed': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('before', type=Path)
    parser.add_argument('after', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    try:
        result = compare_search_transition(PriceVisitQueue._read(args.before), PriceVisitQueue._read(args.after))
        PriceVisitQueue._write(args.output, result)
        print(json.dumps(result))
    except PriceQueueError as exc:
        parser.exit(2, f'Search comparison blocked: {exc}\n')


if __name__ == '__main__':
    main()
