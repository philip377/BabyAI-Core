"""Read-only additive filter proposals when the product panel exceeds its read limit.

This distinct context contains observed search words and a product-row count,
never a substitute empty product snapshot or evidence of candidate absence.
"""
import json

from .ginger_price_queue import PriceQueueError
from .ginger_row_filter_prepare import _propose_word_filters


_CONTEXT_FIELDS = {"SearchWords", "ProductPanelHandle", "ProductRows", "QueryWordsMatchSource",
                   "CanMatch", "SearchCompletionVerified"}
_PANEL_FIELDS = {"Source", "PanelHandle", "Candidates", "IdentityVerified", "CanMatch", "SearchCompletionVerified"}
_SOURCE_FIELDS = {"TableHandle", "ObservedRowPosition", "DocumentNumber", "Supplier", "Title",
                  "PriceText", "LinkedProductText", "IdentityVerified", "CanMatch"}
_ROW_FIELDS = {"ObservedPosition", "Title", "DescriptionText", "Selected", "Checked", "StateImage", "IdentityVerified"}


def _fields(value, required, optional=frozenset()):
    if type(value) is not dict or not required <= value.keys() or value.keys() - required - optional:
        raise PriceQueueError("Incomplete or unsupported filter-only fields")


def _positive_integer(value):
    return type(value) is int and value > 0


def _text(value, limit, *, required=True):
    return isinstance(value, str) and len(value) <= limit and (not required or bool(value.strip()))


def _panel(context):
    _fields(context, _CONTEXT_FIELDS)
    if (context["CanMatch"] is not False or context["SearchCompletionVerified"] is not False
            or context["QueryWordsMatchSource"] is not True
            or type(context["ProductRows"]) is not int or context["ProductRows"] <= 256
            or not _positive_integer(context["ProductPanelHandle"])):
        raise PriceQueueError("Invalid read-only product-overflow context")
    panel = context["SearchWords"]
    _fields(panel, _PANEL_FIELDS)
    if (panel["CanMatch"] is not False or panel["IdentityVerified"] is not False
            or panel["SearchCompletionVerified"] is not False
            or not _positive_integer(panel["PanelHandle"])
            or panel["PanelHandle"] == context["ProductPanelHandle"]):
        raise PriceQueueError("Invalid read-only search-word panel")
    source = panel["Source"]
    _fields(source, _SOURCE_FIELDS)
    if (source["CanMatch"] is not False or source["IdentityVerified"] is not False
            or not _positive_integer(source["TableHandle"])
            or type(source["ObservedRowPosition"]) is not int or not 1 <= source["ObservedRowPosition"] <= 500):
        raise PriceQueueError("Invalid read-only filter source")
    if any(not _text(source[field], 512) for field in
           ("DocumentNumber", "Supplier", "Title", "PriceText", "LinkedProductText")):
        raise PriceQueueError("Missing or oversized filter source text")

    rows = panel["Candidates"]
    if not isinstance(rows, list) or not 1 <= len(rows) <= 256:
        raise PriceQueueError("Invalid search word count")
    seen = set()
    for position, row in enumerate(rows, 1):
        _fields(row, _ROW_FIELDS, {"DefaultAction", "Bounds"})
        if (type(row["ObservedPosition"]) is not int or row["ObservedPosition"] != position
                or row["IdentityVerified"] is not False or type(row["Selected"]) is not bool
                or not _text(row["Title"], 512) or not _text(row["DescriptionText"], 2048, required=False)
                or (row["Checked"] is not None and type(row["Checked"]) is not bool)
                or type(row["StateImage"]) is not int or row["StateImage"] not in (0, 1, 2)
                or (row["StateImage"] == 2) != (row["Checked"] is True)):
            raise PriceQueueError("Invalid search word or checkbox state")
        if row["Title"] in seen:
            raise PriceQueueError("Ambiguous duplicate search word")
        seen.add(row["Title"])
        if "DefaultAction" in row and row["DefaultAction"] is not None and not _text(row["DefaultAction"], 2048, required=False):
            raise PriceQueueError("Invalid search word action metadata")
        if "Bounds" in row:
            bounds = row["Bounds"]
            if (not isinstance(bounds, list) or len(bounds) != 4
                    or any(type(value) is not int or not -(2**31) <= value < 2**31 for value in bounds)
                    or bounds[2] < 0 or bounds[3] < 0):
                raise PriceQueueError("Invalid search word bounds")
    return panel


def propose_filter_only(context):
    """Validate overflow evidence, then propose additions using observed words only."""
    panel = _panel(context)
    try:
        return _propose_word_filters(panel)
    except ValueError as exc:
        raise PriceQueueError("Unsupported search words for additive preparation") from exc


def read_filter_only_json(text):
    """Strict decoding for the explicit CLI mode; duplicate keys cannot be repaired."""
    if not isinstance(text, str) or len(text) > 2_000_000:
        raise PriceQueueError("Filter-only snapshot too large")

    def unique_object(pairs):
        value = {}
        for key, item in pairs:
            if key in value:
                raise PriceQueueError("Duplicate filter-only JSON field")
            value[key] = item
        return value

    def invalid_constant(_):
        raise PriceQueueError("Non-finite filter-only JSON value")

    try:
        value = json.loads(text, object_pairs_hook=unique_object, parse_constant=invalid_constant)
    except (ValueError, RecursionError) as exc:
        raise PriceQueueError("Invalid filter-only JSON") from exc
    if type(value) is not dict:
        raise PriceQueueError("Filter-only JSON must be an object")
    return value
