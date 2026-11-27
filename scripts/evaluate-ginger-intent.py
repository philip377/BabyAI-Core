"""Optional native-model evaluation; never opens Ginger or the agent/tool runtime."""
import argparse
import json
import time
from pathlib import Path

from babyai.ginger_chat_intent import review_intent
from babyai.resident_native_brain import ResidentNativeBrainProvider


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--runtime', type=Path, required=True)
    parser.add_argument('--gpu-layers', type=int, default=0)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    cases = json.loads((Path(__file__).resolve().parents[1] /
        'tests/fixtures/ginger_chat_intent_cases.json').read_text(encoding='utf-8'))
    model = ResidentNativeBrainProvider(model_path=args.model, runtime_path=args.runtime,
        n_gpu_layers=args.gpu_layers, n_threads=4)
    report = dict(desktop_actions=False, weights_changed=False, cases=[])
    args.output.parent.mkdir(parents=True, exist_ok=True)
    try:
        for case in cases:
            start = time.monotonic()
            try:
                result = review_intent(model, case['message'])
            except Exception as exc:
                result = dict(error=str(exc))
            passed = result.get('intent') == case['expected']
            report['cases'].append(dict(**case, result=result, passed=passed,
                                       seconds=round(time.monotonic()-start, 2)))
            report['all_passed'] = all(item['passed'] for item in report['cases'])
            args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
            print(json.dumps(dict(**case, observed=result.get('intent'), passed=passed), ensure_ascii=False), flush=True)
    finally:
        model.close()
    return 0 if report['all_passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
