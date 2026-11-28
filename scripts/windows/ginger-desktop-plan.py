import argparse
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src"))
from babyai.ginger_row_filter_prepare import propose_filters
from babyai.ginger_filter_only import propose_filter_only, read_filter_only_json

parser = argparse.ArgumentParser(description="Propose additive filters from a read-only Ginger snapshot")
parser.add_argument("context", type=Path)
parser.add_argument("--filter-only", action="store_true", help="Accept the distinct product-overflow context")
args = parser.parse_args()
if args.filter_only and args.context.stat().st_size > 2_000_000:
    parser.error("Filter-only snapshot too large")
text = args.context.read_text(encoding="utf-8-sig")
context = read_filter_only_json(text) if args.filter_only else json.loads(text)
result = propose_filter_only(context) if args.filter_only else propose_filters(context)
print(json.dumps(result, ensure_ascii=True))
