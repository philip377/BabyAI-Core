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


## Confirmed Ginger 4.14.3.92 workflow (owner screenshot, October 2026)

The operator works in "Прайс-лист поставщика". The screenshot shows supplier
"Олег 23", price list #108803, with approximately 1654 source rows.

1. Pick an as-yet-unmatched supplier row in "Записи прайс-листа" (top-left).
2. Ginger tokenizes the source title in "Сопоставление с каталогом"
   (lower-left, columns "Слово" and "Поиск").
3. The operator sets a star next to relevant individual word rows in
   the lower-left table to drive Ginger's search. "Поиск" may contain
   spelling variations / synonyms for the word.
4. The lower-right "Товар / Цена" list displays candidate catalogue items.
5. Choose the correct internal catalogue item on the right, then use the
   existing linking operation to connect it to the supplier row.

Important: this is not merely fuzzy comparison outside Ginger. UNIX needs a
permissioned UI driver to read source rows, inspect word rows, select stars,
read the resulting candidates, select the proper Ginger product ID, and
invoke the link operation. Never assume button meaning from icon or position.

### Added read-only word-search planner

ginger_search_plan.plan_ginger_word_search(...) produces primary and fallback
sets of visible word-row indices. Supply visible_words read from Ginger instead
of assuming Ginger tokenization matches the supplier raw title.
Distinctive name words take priority over generic packaging (EDP/ML/TESTER);
the catalogue matcher separately checks size, concentration, flanker,
tester status and barcode.

Fallbacks only broaden the candidate search; they do not approve any link.
The program's synonym expansion and custom UI controls still require a live
supervised Windows test.

### Integration acceptance criteria

- Act only on the explicitly selected supplier price list.
- Verify Ginger window/control identity before interacting.
- Read the selected source row and the associated word rows freshly.
- Start with read-only dry-run: no star toggles or linking until approval.
- Display exact supplier title and target canonical ID/name before writing.
- After each approved link, read the UI again to verify it was saved.
- Pause on ambiguous variants, missing specs, stale focus, unexpected
  dialogs, and changed price-list rows.
- Do not write to Ginger SQL/server directly; commit through the existing
  authorized Windows client after explicit approval.
- Never claim success from a star or a click without verifying the saved link.

### Non-goals for the current PR

No live Ginger GUI automation, accessibility-tree inspection, SQL access,
imports, mass commits, or background job processing is included. These
require owner-supervised tests on the actual Windows machine.


### Read-only inspection command (Windows)

From a local checkout of the feature branch with the supplier price list
visible in Ginger4, run:

~~~powershell
powershell -ExecutionPolicy Bypass -File .\scripts\windows\ginger-uia-probe.ps1 |
  Out-File -FilePath .\ginger-uia-tree.json -Encoding utf8
~~~

The output lists UI Automation control types, IDs, classes and screen bounds,
with NO control names by default. It makes no mouse/keyboard calls, changes no
stars, and creates no mappings. Sharing the generated JSON still warrants a
quick privacy check for window/process metadata. Do not enable -IncludeNames
unless you explicitly agree to reveal UI element names.

This inspection is not a working Ginger automation adapter. Some custom
controls may show only as opaque panes; in that case, a supervised alternative
is needed rather than making blind coordinate clicks.
