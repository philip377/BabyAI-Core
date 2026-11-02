"""Prepare explicitly requested filter removal; never execute UI input."""
from .ginger_filter_plan import _query
from .ginger_price_queue import PriceQueueError
from .ginger_row_filter_prepare import propose_filters
from .ginger_row_journal import RowNavigationJournal, snapshot


def propose_removal(row_directory, report, context, *, expected_source, words):
    """Bind a caller's explicit request to the exact observed row and word panel.

    Free-text feedback is never interpreted here. The returned steps still need
    the existing FilterPlan controller's fresh checks and explicit confirmation.
    """
    digest, selected, _, pending, *_ = RowNavigationJournal(row_directory)._state()
    observed_digest, observed_selected, _ = snapshot(report)
    if pending is not None or (digest, selected) != (observed_digest, observed_selected):
        raise PriceQueueError("Pending navigation or changed row table")
    _query(context)
    propose_filters(context)  # Includes exact source-title/word-panel validation.
    source = context["SearchWords"]["Source"]
    actual = report["rows"][selected - 1]["Observation"]
    keys = ("TableHandle", "ObservedRowPosition", "DocumentNumber", "Supplier",
            "Title", "PriceText", "LinkedProductText")
    if not isinstance(expected_source, dict) or set(expected_source) != set(keys):
        raise PriceQueueError("Complete expected source required")
    if any(type(expected_source[k]) is not type(actual[k]) or
           expected_source[k] != actual[k] or source[k] != actual[k] for k in keys):
        raise PriceQueueError("Removal request belongs to another source")
    if (not isinstance(words, list) or not 1 <= len(words) <= 32 or
            any(not isinstance(w, str) or not w.strip() for w in words) or
            len(set(words)) != len(words)):
        raise PriceQueueError("Explicit unique words required")
    rows = context["SearchWords"]["Candidates"]
    steps, already_clear = [], []
    for word in words:
        matches = [row for row in rows if row["Title"] == word]
        if len(matches) != 1:
            raise PriceQueueError("Requested word missing or ambiguous")
        row = matches[0]
        if row["Checked"] is True:
            steps.append({"word": word, "enabled": False})
        elif row["Checked"] is False:
            already_clear.append(word)
        else:
            raise PriceQueueError("Requested filter state is unknown")
    return {"policy": "explicit_removal_v1", "steps": steps,
            "already_clear": already_clear, "row_snapshot_digest": digest,
            "source": dict(expected_source), "confirmation_required": True,
            "can_write": False, "can_match": False, "absence_proven": False,
            "search_completion_verified": False}
