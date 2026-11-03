import json, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "src"))
from babyai.ginger_row_filter_prepare import propose_filters
context = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
print(json.dumps(propose_filters(context), ensure_ascii=True))
