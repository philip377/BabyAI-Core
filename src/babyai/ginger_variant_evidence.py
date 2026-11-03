"""Explicit variant words are evidence, never inferred product identity."""
import re


def variant_evidence(title):
    words = set(re.findall(r"\b(?:noir|intense|elixir|absolue)\b", title.casefold()))
    audience = set()
    for value, pattern in (
        ("homme", r"\bpour\s+homme\b|\bfor\s+men\b|\(\s*муж\s*\)"),
        ("femme", r"\bpour\s+femme\b|\bfor\s+women\b|\(\s*жен\s*\)"),
    ):
        if re.search(pattern, title, re.I): audience.add(value)
    return words, audience


def unresolved_variant(source, candidate):
    left, right = variant_evidence(source), variant_evidence(candidate)
    return left != right or len(left[1]) > 1 or len(right[1]) > 1


def explain_variants(source, candidate):
    left, right = variant_evidence(source), variant_evidence(candidate)
    differences, uncertainties = [], []
    for index, field, label in ((0, "variant_words", "Обозначение версии"),
                                (1, "audience", "Мужской/женский вариант")):
        a, b = left[index], right[index]
        if not a and not b: continue
        item = {"field": field, "source": ", ".join(sorted(a)) or None,
                "candidate": ", ".join(sorted(b)) or None}
        if index == 1 and (len(a) > 1 or len(b) > 1):
            uncertainties.append(dict(item, message=label + ": неоднозначные обозначения"))
        elif not a or not b:
            uncertainties.append(dict(item, message=label + ": указан только с одной стороны; равенство не установлено"))
        elif a != b:
            differences.append(dict(item, message=label + ": обозначения различаются"))
    return differences, uncertainties
