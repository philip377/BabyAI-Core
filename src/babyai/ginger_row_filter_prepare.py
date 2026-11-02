"""Prepare additive word filters for the exact journalled row; no UI input."""
import argparse
import json
from pathlib import Path
import re
from .ginger_filter_plan import FilterPlan, _query, _read, _save
from .ginger_price_queue import PriceQueueError
from .ginger_row_journal import RowNavigationJournal, snapshot
from .ginger_search_plan import plan_ginger_word_search
from .ginger_supplier_matcher import SupplierPosition


POLICY_V1 = "additive_primary_v1"
POLICY_V2 = "additive_primary_v2"


def verification_only_indices(words):
    """Explicit packaging/condition phrases only; never erase arbitrary parentheses."""
    phrases = (
        "с крышкой", "без крышки", "не полный", "неполный", "немного помят",
        "мятая коробка", "мятая упаковка", "поврежденная упаковка",
        "повреждённая упаковка", "без коробки", "with cap", "without cap",
        "no cap", "damaged box", "without box",
    )
    normalized = [word.casefold() for word in words]
    excluded = set()
    for phrase in phrases:
        parts = phrase.split()
        for index in range(len(words)-len(parts)+1):
            if normalized[index:index+len(parts)] == parts:
                excluded.update(range(index,index+len(parts)))
    return excluded


def propose_filters(context, *, policy=POLICY_V2):
    if policy not in (POLICY_V1, POLICY_V2):
        raise PriceQueueError("Unknown filter policy")
    _query(context)
    panel = context["SearchWords"]
    rows = panel["Candidates"]
    words = [row["Title"] for row in rows]
    tokens = lambda value: re.findall(r"[^\W\d_]+|\d+|[^\s]", value.casefold())
    expected = tokens(panel["Source"]["Title"])
    collapsed = []
    seen_letters = set()
    for token in expected:
        if not (token.isalpha() and token in seen_letters):
            collapsed.append(token)
        if token.isalpha():
            seen_letters.add(token)
    actual = tokens(" ".join(words))
    if not words or (actual != expected and actual != collapsed):
        raise PriceQueueError("Search words do not describe selected supplier row")
    verification = verification_only_indices(words) if policy == POLICY_V2 else set()
    proposed = plan_ginger_word_search(
        SupplierPosition(panel["Source"]["Supplier"], panel["Source"]["Title"]), [],
        visible_words=words, visible_search_columns=[row["DescriptionText"] for row in rows],
        excluded_indices=sorted(verification))
    # Do not interpret supplier abbreviations such as ash/l or enable their pieces.
    uncertain = set()
    for index, word in enumerate(words):
        if word == "/":
            uncertain.update(i for i in (index-1, index, index+1) if 0 <= i < len(words))
    eligible = [index for index in proposed.primary_indices
                if index not in uncertain and words.count(words[index]) == 1]
    steps = [{"word": words[index], "enabled": True} for index in eligible if rows[index]["Checked"] is not True]
    return {"policy": policy, "steps": steps,
            "verification_only_words": [words[i] for i in sorted(verification)],
            "preserved_verification_filters": [words[i] for i in sorted(verification) if rows[i]["Checked"] is True],
            "eligible_words": [words[index] for index in eligible],
            "ambiguous_words_preserved": [words[index] for index in sorted(uncertain)],
            "existing_filters_preserved": True, "catalogue_frequency_available": False,
            "status": "plan_proposed" if steps else "filters_already_set_review_results" if eligible else "manual_review_no_terms",
            "can_write": False, "can_match": False, "absence_proven": False,
            "search_completion_verified": False}


def prepare_row_filters(row_directory, report, context, plan_directory, proposal_path):
    journal = RowNavigationJournal(row_directory)
    digest, selected, _, pending, _, _, _, _ = journal._state()
    observed_digest, observed_selected, _ = snapshot(report)
    if pending is not None or digest != observed_digest or selected != observed_selected:
        raise PriceQueueError("Pending navigation or changed journalled row table")
    source = context["SearchWords"]["Source"]
    _query(context)
    actual = report["rows"][selected-1]["Observation"]
    keys = ("TableHandle", "ObservedRowPosition", "DocumentNumber", "Supplier", "Title", "PriceText", "LinkedProductText")
    if any(source[key] != actual[key] for key in keys):
        raise PriceQueueError("Filter context belongs to another row")
    proposal = propose_filters(context)
    # Publication of a proposal is not permission to send input. Controller still requires confirmation.
    proposal.update(source_position=selected, row_snapshot_digest=digest)
    _save(Path(proposal_path), proposal)
    if proposal["steps"]:
        FilterPlan.create(plan_directory, context, report["process_id"], proposal["steps"])
    return proposal


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--row-directory", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--plan-directory", type=Path, required=True)
    parser.add_argument("--proposal", type=Path, required=True)
    args = parser.parse_args()
    report = _read(args.report)
    result = prepare_row_filters(args.row_directory, report["rows_report"], report["context"], args.plan_directory, args.proposal)
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__": main()
