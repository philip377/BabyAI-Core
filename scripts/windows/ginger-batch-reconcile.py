"""Read-only reconciliation of a stopped preparation, under the batch lock."""
from pathlib import Path
import argparse
import json
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src"))
from babyai.ginger_batch import batch_lock, reconcile_stopped_prepare
from babyai.ginger_filter_plan import _read

parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument("--row-directory",type=Path,required=True)
parser.add_argument("--run-directory",type=Path,required=True)
parser.add_argument("--fresh-report",type=Path,required=True)
args=parser.parse_args()
with batch_lock(args.row_directory):
    result=reconcile_stopped_prepare(args.row_directory,args.run_directory,_read(args.fresh_report))
print(json.dumps(result,ensure_ascii=False))
