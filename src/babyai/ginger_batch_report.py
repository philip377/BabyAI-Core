"""Read-only Russian summary of a completed or stopped bounded pass."""
import argparse
from pathlib import Path
from .ginger_filter_plan import _read
from .ginger_price_queue import PriceQueueError
from .ginger_review_report import _text

_LABELS={"NextReviewed":"Переход к следующей строке", "PrepareFilters":"Подготовка фильтров",
         "FilterStep":"Один шаг фильтра", "FinishReview":"Сохранение проверки строки"}


def _elapsed(value):
    if type(value) is not int or value<0: raise PriceQueueError("Invalid elapsed duration")
    return f"{value/1000:.2f} с"


def render_batch_report(run_directory):
    directory=Path(run_directory)
    success=(directory/"result.json").is_file()
    stopped=(directory/"stopped.json").is_file()
    if success == stopped: raise PriceQueueError("Exactly one completed or stopped outcome required")
    result=_read(directory/("result.json" if success else "stopped.json"))
    if success and (result.get("status")!="bounded_pass_complete" or any(result.get(key) is not False for key in ("can_write","products_processed","human_approved"))):
        raise PriceQueueError("Invalid batch outcome")
    if stopped and result.get("automatic_retry_allowed") is not False:
        raise PriceQueueError("Stopped pass cannot permit automatic retry")
    positions=result.get("reviewed_positions")
    if (not isinstance(positions,list) or len(positions)>3 or len(set(positions))!=len(positions)
            or any(type(position) is not int or not 1<=position<=500 for position in positions)):
        raise PriceQueueError("Invalid reviewed positions")
    lines=["# Проход Ginger", "", "Итог: " + ("ограниченный проход завершён." if success else "проход остановлен, повтор действий не разрешён."),
           "Строки с сохранёнными отчётами: " + (", ".join(map(str,positions)) or "нет") + ".",
           "Сохранённый отчёт не подтверждает сопоставление. Товары не считаются обработанными.", ""]
    timing=result.get("timing")
    if timing is not None: lines += ["Общее время: " + _elapsed(timing["elapsed_ms"]) + ".", ""]
    else: lines += ["В этой версии журнала общее время не измерялось.", ""]
    intents=sorted(directory.glob("action-*-intent.json"))
    for index,path in enumerate(intents):
        if path.name!=f"action-{index:03d}-intent.json": raise PriceQueueError("Missing batch action")
        intent=_read(path);operation=intent.get("operation")
        if operation not in _LABELS: raise PriceQueueError("Unknown batch action")
        observed=directory/f"action-{index:03d}-observed.json"
        failure=directory/f"action-{index:03d}-failure.json"
        if observed.exists() and failure.exists(): raise PriceQueueError("Conflicting action outcomes")
        if observed.exists():
            record=_read(observed);execution=record.get("execution");status="результат записан"
        else:
            if success or index!=len(intents)-1: raise PriceQueueError("Missing observed action")
            record=_read(failure) if failure.exists() else {};execution=record if failure.exists() else None
            status="остановка; исход действия требует проверки"
        if execution is not None:
            if execution.get("operation")!=operation or execution.get("started_at_utc")!=intent.get("started_at_utc"):
                raise PriceQueueError("Action timing belongs to another intent")
            duration=_elapsed(execution["elapsed_ms"])
        else: duration="время не измерялось"
        lines.append(f"- {_LABELS[operation]}: {status}; {duration}.")
    if stopped:
        lines += ["", "Причина: " + _text(result.get("error","не указана")),
                  "Подробная техническая диагностика хранится локально в файлах action-…-failure.json."]
    return "\n".join(lines)+"\n"


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-directory",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args()
    report=render_batch_report(args.run_directory)
    with args.output.open("x",encoding="utf-8") as stream: stream.write(report)
    print(str(args.output.resolve()))


if __name__ == "__main__": main()
