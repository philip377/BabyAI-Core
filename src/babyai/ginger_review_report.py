"""Russian local report from a validated review queue; no UI or approval actions."""
import argparse
import html
import re
from pathlib import Path
from .ginger_batch import batch_lock
from .ginger_filter_plan import _read
from .ginger_review_queue import ReviewQueue
from .ginger_price_queue import PriceQueueError


def _text(value):
    if value is None or value == "": return "не указано"
    if type(value) is bool: return "да" if value else "нет"
    text = " ".join(str(value).split())
    return re.sub(r"([\\`*_{}\[\]()#+.!|>~-])", r"\\\1", html.escape(text))


def render_report(row_directory):
    queue = ReviewQueue(row_directory)
    state, _, queued = queue._validated()
    if state[3] is not None:
        raise PriceQueueError("Pending navigation; report cannot describe a settled checkpoint")
    rows = _read(queue.journal.directory / "plan.json")["rows"]
    lines = ["# Проверка номенклатуры Ginger", "",
             "Это сохранённые наблюдения, а не подтверждённые сопоставления.",
             "Завершение поиска Ginger не подтверждено. Пустой список не доказывает отсутствие товара.",
             "Цены ниже — текст из прайса без пересчёта валюты или наценки.", "",
             f"Отчётов: {len(queued)}. Текущая строка журнала: {state[1]}.", ""]
    for position, report in sorted(queued.items()):
        source = rows[position-1]["Observation"]
        candidates = report["review"]["candidates"]
        lines += [f"## Строка {position}: {_text(source['Title'])}", "",
                  f"Прайс: {_text(source['DocumentNumber'])}. Поставщик: {_text(source['Supplier'])}.",
                  f"Цена в прайсе: {_text(source['PriceText'])}.",
                  f"Наблюдаемых кандидатов: {len(candidates)}. Требуется ручная проверка.", ""]
        observation=report.get("search_observation")
        if observation:
            lines += [f"Кандидатов при подготовке: {observation['before_count']}; после фильтров: {observation['after_count']}.", ""]
            if observation["possible_overfiltering"]:
                lines += ["После фильтров наблюдаемые кандидаты исчезли. Нужна проверка ограничений поиска; это не доказательство отсутствия товара.", ""]
            if observation["preserved_verification_filters"]:
                lines += ["Сохранены ранее включённые фильтры комплектации: " + ", ".join(_text(x) for x in observation["preserved_verification_filters"]) + ".", ""]
        if not candidates:
            lines += ["Кандидаты не наблюдались; решение о создании карточки не принято.", ""]
        for index, candidate in enumerate(candidates, 1):
            lines += [f"### Кандидат {index}: {_text(candidate['candidate_title'])}", ""]
            for group, label in (("differences", "Различие"), ("uncertainties", "Неопределённость")):
                for item in candidate[group]:
                    details = ""
                    if "source" in item or "candidate" in item:
                        details = f" (прайс: {_text(item.get('source'))}; каталог: {_text(item.get('candidate'))})"
                    lines.append(f"- {label}: {_text(item['message'])}{details}")
            lines += ["", "Совпадение товара не подтверждено. Запись и связывание не разрешены этим отчётом.", ""]
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--row-directory", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    with batch_lock(args.row_directory):
        report = render_report(args.row_directory)
        with args.output.open("x", encoding="utf-8") as stream:
            stream.write(report)
    print(str(args.output.resolve()))


if __name__ == "__main__": main()
