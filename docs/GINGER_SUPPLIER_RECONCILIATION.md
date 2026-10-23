# Ginger supplier reconciliation — safe v1 foundation

## Business meaning

Ginger holds our canonical catalogue. A vendor's price list may call the same
fragrance something completely different or use another language, word order,
concentration abbreviation, bottle size or article code. This matcher proposes
which **existing canonical Ginger product** a **supplier row** corresponds to.

This is NOT matching an incoming invoice to stock, and it is NOT importing
stock or updating prices.

## Inputs

- Canonical catalogue snapshot: unique Ginger product ID (SKU), name, and,
  where available, brand, volume (ml), concentration, tester status and barcode.
- Supplier rows: supplier ID, their product/article ID (if any), raw title, and
  the same optional attributes.
- Approved aliases: local JSON mapping for one supplier's article/title to a
  canonical Ginger product ID. This is written only on explicit confirmation.

No direct SQL access or Ginger client automation is implemented here. Do not
connect a write-capable Ginger database account to this matching module.

## API example

Use the following Python code after exporting data into these structures:

~~~python
from pathlib import Path
from babyai.ginger_supplier_matcher import (
    CatalogPosition, SupplierPosition, GingerSupplierMatcher, SupplierAliasStore,
)

catalogue = [
    CatalogPosition("ginger-101", "KHAMRAH EDP 100ML", brand="LATTAFA"),
    CatalogPosition("ginger-102", "KHAMRAH QAHWA EDP 100ML", brand="LATTAFA"),
]
row = SupplierPosition(
    supplier_id="supplier-a", position_id="A-294",
    title="LATTAFA KHAMRAH 100 ML парфюмированная вода",
)
aliases = SupplierAliasStore(Path.home() / ".babyai" / "ginger" / "aliases.json")
matcher = GingerSupplierMatcher(catalogue, aliases)
proposal = matcher.recommend(row)
print(proposal.status, proposal.candidates)
# Human confirms the canonical Ginger ID shown in the proposal:
aliases.confirm(row, "ginger-101")
print(matcher.recommend(row).matched_sku)  # ginger-101
~~~

## Output semantics

- known: a previously approved, supplier-scoped mapping whose source name,
  variant and current Ginger catalogue target remain compatible.
- suggested: one strong and clearly leading candidate, still NOT applied.
- review: uncertain/ambiguous candidate list, human must decide.
- unmatched: no compatible candidate; no mapping is invented.

Reject mismatched known bottle volumes, concentrations, tester classifications,
barcodes and multi-pack types. Missing fields lower confidence rather than
proving equality. Keep flankers such as KHAMRAH and KHAMRAH QAHWA distinct.

The alias store is local-only and never writes to Ginger. A vendor's reused
article code cannot automatically inherit its old mapping after the product
name or variant changes. Explicit reassignment requires replace=True.

## Next integration (not implemented in this PR)

1. Obtain a read-only catalogue export of existing Ginger product IDs and
   names, and a sample supplier price list with supplier-specific columns.
2. Map the actual columns into CatalogPosition/SupplierPosition without
   changing the Ginger server.
3. Display side-by-side candidate choices in UNIX, along with raw supplier
   titles and canonical Ginger names. Preview an entire matching batch.
4. After explicit human confirmation, apply the selected mapping through the
   existing authorized Ginger client workflow, verifying post-action results
   from a fresh read. Never guess control IDs or execute unapproved mass changes.
5. Only after acceptance tests, consider an opt-in fast path for previously
   verified aliases. Keep a rollback journal and pause on mismatches.

A green unit test does not prove that Ginger client control automation works.
Client-specific integration needs an actual interaction sample and an owner
Windows acceptance run.
