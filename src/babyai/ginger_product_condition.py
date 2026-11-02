"""Explicit condition phrases only. Absence of a phrase is always unknown."""
import re

# These are evidence labels, not equivalence or purchasing rules.
_PATTERNS = {
    "cap": {
        "present": (r"\bс\s+крышкой\b", r"\bwith\s+cap\b"),
        "absent": (r"\bбез\s+крышки\b", r"\bwithout\s+cap\b", r"\bno\s+cap\b"),
    },
    "box": {
        "present": (r"\bв\s+коробке\b", r"\bwith\s+box\b"),
        "absent": (r"\bбез\s+коробки\b", r"\bwithout\s+box\b", r"\bno\s+box\b"),
        "damaged": (r"\bмят(?:ая|ой)\s+коробк[аи]\b", r"\bdamaged\s+box\b"),
    },
    "fill": {
        "partial": (r"\bне\s*полный\b", r"\bpartially\s+used\b", r"\bpartial(?:ly)?\s+full\b"),
        "full": (r"(?<!не )(?<!не)\bполный\s+флакон\b", r"\bfull\s+bottle\b"),
    },
    "packaging": {
        "damaged": (r"\bнемного\s+помят\b", r"\b(?:мятая|поврежд[её]нная)\s+упаковка\b", r"\bdamaged\s+packaging\b"),
    },
}
_LABELS={"cap":"Крышка", "box":"Коробка", "fill":"Наполнение флакона", "packaging":"Состояние упаковки"}
_VALUES={"present":"есть", "absent":"нет", "damaged":"повреждена", "partial":"неполный", "full":"полный"}


def condition_evidence(title):
    if not isinstance(title,str) or not title.strip(): raise ValueError("Title is required")
    result={}
    for field, values in _PATTERNS.items():
        found=[]
        for value, patterns in values.items():
            matches = [match for pattern in patterns for match in re.finditer(pattern,title,re.I)]
            if any(not re.search(r"\b(?:не|not|no)\s*$",title[:match.start()],re.I) for match in matches):
                found.append(value)
        result[field]=found
    return result


def explain_conditions(source_title, candidate_title):
    source, candidate=condition_evidence(source_title),condition_evidence(candidate_title)
    differences, uncertainties=[],[]
    for field, label in _LABELS.items():
        left,right=source[field],candidate[field]
        if not left and not right: continue
        item={"field":"condition_"+field,
              "source":", ".join(_VALUES[x] for x in left) if left else None,
              "candidate":", ".join(_VALUES[x] for x in right) if right else None}
        if len(left)>1 or len(right)>1:
            uncertainties.append(dict(item,message=label+": противоречивые признаки, нужна проверка"))
        elif not left or not right:
            uncertainties.append(dict(item,message=label+": не хватает данных; отсутствие слов не означает обычную комплектацию"))
        elif left!=right:
            differences.append(dict(item,message=label+": явно указанные признаки различаются"))
    return differences,uncertainties
