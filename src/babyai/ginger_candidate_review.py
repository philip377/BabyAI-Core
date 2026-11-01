"""Local review of native candidate snapshots. No UI, catalogue writes or inferred SKUs."""
import argparse
import json
from pathlib import Path
from .ginger_supplier_matcher import SupplierPosition, CatalogPosition, explain_candidate


def review_snapshot(snapshot):
    if not isinstance(snapshot, dict):
        raise ValueError("Snapshot must be an object")
    source = snapshot.get("Source")
    rows = snapshot.get("Candidates")
    if not isinstance(source, dict) or not isinstance(rows, list) or len(rows) > 256:
        raise ValueError("Invalid source or candidate rows")
    for obj, flags in ((snapshot, ("CanMatch", "IdentityVerified", "SearchCompletionVerified")),
                       (source, ("CanMatch", "IdentityVerified"))):
        if any(obj.get(flag) is not False for flag in flags):
            raise ValueError("Unsupported authorization or freshness claim")
    for field in ("Supplier", "DocumentNumber", "Title", "PriceText", "LinkedProductText"):
        value = source.get(field)
        if not isinstance(value, str) or not value.strip() or len(value) > 512:
            raise ValueError("Missing source text")
    if type(source.get("ObservedRowPosition")) is not int or not 1 <= source["ObservedRowPosition"] <= 500:
        raise ValueError("Invalid source position")
    reviews = []
    position = SupplierPosition(source["Supplier"], source["Title"])
    for index, row in enumerate(rows, 1):
        if (not isinstance(row, dict) or type(row.get("ObservedPosition")) is not int
                or row["ObservedPosition"] != index or row.get("IdentityVerified") is not False
                or not isinstance(row.get("Title"), str) or not row["Title"].strip()
                or len(row["Title"]) > 512):
            raise ValueError("Invalid candidate observation")
        # A row index is not a catalogue SKU; do not manufacture one.
        review = explain_candidate(position, CatalogPosition("", row["Title"]))
        reviews.append({"observed_position": index, **review})
    return {"document_number": source["DocumentNumber"], "source_position": source["ObservedRowPosition"],
            "candidates": reviews, "can_write": False, "can_match": False,
            "absence_proven": False, "search_completion_verified": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.snapshot.stat().st_size > 2_000_000:
        raise ValueError("Snapshot too large")
    data = json.loads(args.snapshot.read_text(encoding="utf-8-sig"))
    report = review_snapshot(data.get("candidate_snapshot", data))
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)


if __name__ == "__main__":
    main()
