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


## Missing catalogue product: owner demonstration (23 October 2026)

The screenshots explicitly demonstrate a **second** operator flow in
Ginger 4.14.3.92. It must not be mistaken for linking a generic fragrance.

Supplier price-list item: "Bruno Banani Man Intense edP 30ml".
An existing Ginger base-template item was:
"Bruno Banani BRUNO BANANI (муж) edp 30ml".
Existing "LOYAL MAN" and base "BRUNO BANANI" items are NOT the same perfume
as MAN INTENSE and must not be linked to the supplier row.

Observed manual flow:

1. Keep that supplier row selected in the upper-left price list.
2. Put stars by ALL seven visible words: Bruno / Banani / Man /
   Intense / edP / 30 / ml. The lower-right search returns zero candidates.
3. Remove the star specifically from "Intense". The relaxed search
   returns several related catalogue items. This is only evidence of
   possible templates, NOT evidence that the source equals a result.
4. Check independently whether the named perfume physically exists. The
   owner provided an online product listing/photo showing the Man Intense
   scent, 30 ml, edP. A negative Ginger search alone is insufficient proof.
5. Right-click the correct similar existing base catalogue item in the
   bottom-right table and choose "Добавить с текущими значениями"
   (not "Добавить" if current values are required).
6. In "Добавить товар", choose group "BRUNO BANANI".
7. Keep the group-based Ginger naming convention and insert INTENSE
   **after BRUNO BANANI**, before "(муж) edp 30ml":
   "Bruno Banani BRUNO BANANI INTENSE (муж) edp 30ml".
   INTENSE is the fragrance variant, not the product concentration/type.
8. Leave barcode empty; set both "Цена 1" and "Цена 2" to 0;
   leave "Описание" empty. Press OK only after operator approval.
9. Repeat the full-star search; the newly created item must now be visible
   on the right. Re-select the original supplier row (not a neighbor) and
   double-click the verified new canonical item.
10. Verify that the supplier row's "Товар" cell now identifies the new
    BRUNO BANANI INTENSE item and that a fresh right-hand catalogue view
    points to the same canonical product.

The new read-only code covers two distinct decisions:
- ginger_variant_search.plan_variant_search_passes: full all-star attempt,
  then a template-discovery pass without the named variant. Empty results
  do not by themselves prove the product is missing.
- ginger_catalog_creation.draft_missing_variant: can only generate a draft
  if the operator confirms the exact catalogue search was checked,
  independently verified the perfume identity, and checked the selected
  supplier row. Requires an existing, appropriate template with matching
  volume/concentration/packaging, a matching group and a nonduplicate
  catalogue name. It refuses to copy from incompatible source products.

The draft contains "group", "new_title", empty barcode and description,
zero prices and a **requires_explicit_approval** flag. No Ginger write,
right-click, OK press, item creation, or double-click occurs in this PR.

### Technical separation

Ginger is an operational warehouse/customer system. Never interpret
an unsuccessful search or a web result as authorization to create a SKU.
A future supervised Windows adapter needs independent, cancellable
permissions for (a) interactive search, (b) catalog creation, and (c) linking.
Before each side effect it must verify the selected supplier row and
current window state. After each side effect it must re-read and validate.
It must never write directly to the existing Dubrovka/Ginger server.

A user-approved bulk policy can eventually allow handling straightforward,
verified EXISTING positions; creating NEW products remains approval-gated
until an actual Ginger owner acceptance test establishes safe rollback and
duplicate prevention.


## October 23 UIA tree diagnosis and next read-only check

The owner's ginger-uia-tree-v2.json was generated successfully:
- ControlView: 152 elements, deepest 24 (depth limit reached).
- RawView: 152 elements, deepest 24 (depth limit reached).
- Both view trees report the same hierarchy; their control types are panes
  rather than useful DataGrid/ListItem typed providers.
- Crucially, **two real native SysListView32 controls** were found at the
  bottom matching area of the Ginger4 window (2560 x 1440 screen):
  - at x=0, y=1116, width=1280, height=302: the left search-word/synonym table;
  - at x=1284, y=1092, width=1276, height=326: the right candidate catalogue.
- The main supplier price list spans x~2 to 2558, y~374 to 1040 in the
  recorded UI tree, but appeared as generic nested WinForms panes. The UIA
  file does NOT expose source row text or identity.

Read-only follow-up: scripts/windows/ginger-listview-probe.ps1.
This specifically discovers native listviews and reports UIA supported pattern
names, Win32 HWND availability and the number of visible list items via the
non-mutating LVM_GETITEMCOUNT message with a 250 ms timeout. It does not read
product/customer text or perform input or SQL operations.

Run with Ginger4 showing the same supplier reconciliation page:

~~~powershell
cd "$env:USERPROFILE\Downloads"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\ginger-listview-probe.ps1" |
  Out-File ".\ginger-listview-report.json" -Encoding utf8
~~~

The script needs to be downloaded from this branch first. If item counts or UIA
patterns are missing, use that result to determine whether a supervised Win32
or MSAA inspection is necessary. Do not fall back to blind fixed-coordinate
clicking or assume that a pane exposes the underlying supplier's item IDs.


## Read-only native list-view findings (owner file, 23 October 2026)

The ginger-listview-report.json confirmed:
- Two actual WindowsForms10.SysListView32 native controls have valid HWNDs.
- Left word-search table: 5 rows (native LVM_GETITEMCOUNT returned ok).
- Right internal-catalogue search table: 8 rows (native LVM_GETITEMCOUNT returned ok).
- Both controls expose zero UIA supported patterns, so direct
  SelectionPattern/GridPattern/TextPattern operation is NOT available.

Do not confuse successful native row counts with proof of readable row text
or programmable selection. The next diagnostic attempts MSAA
(Windows Legacy Accessibility) on each ListView without process-memory
injection or input. It samples at most 12 accessible child rows per table.

Download scripts/windows/ginger-msaa-probe.ps1 from this branch and run in
Windows PowerShell with the matching panel visible:

~~~powershell
cd "$env:USERPROFILE\Downloads"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\ginger-msaa-probe.ps1" -MaxSampleRows 12 -IncludeNames | Out-File ".\ginger-msaa-report.json" -Encoding utf8
~~~

- Including -IncludeNames is an explicit owner choice. It can reveal
  names of suppliers and catalogue items; inspect JSON before sending.
- If this reports child labels, the next Windows UI adapter can map row
  identity to candidate names and only then evaluate selection mechanics.
- If MSAA yields no names or no accessible children, investigate a strictly
  scoped Win32 ListView text retrieval adapter or supervised visual path;
  never treat raw row indices as business item identity.
- No existing UI or file is altered by this probe.


### Follow-up: a 0-listview MSAA scan does not prove loss of native access

Owner's ginger-msaa-report.json (23 October 2026) identified process Ginger4
and visited 149 UI Automation controls, but matched **zero** ListView controls.
The earlier ginger-listview-report.json found two native SysListView32 controls,
with 5 and 8 items respectively. Therefore no MSAA text was actually sampled
in this run.

Possible explanations: Ginger was showing a different active tab/form;
the traversal path differed; or nested WinForms controls temporarily changed.
The report cannot distinguish these cases.

The MSAA probe has been updated to enumerate *native child HWNDs* directly
using Win32 EnumChildWindows/GetClassNameW. This does not rely on UIA ControlView
tree depth. Its output includes:
- native_child_windows_seen, class histogram, and listview_count;
- each identified SysListView32 rectangle and native item count;
- MSAA status / accessible child count / optional row-label sample.

Always open the "Прайс-лист поставщика" matching page and leave the bottom
two matching tables visible during the inspection. If listview_count is still
zero, compare top_window_classes and capture a fresh owner screenshot of the
current window; do not assume MSAA or the Ginger installation is defective.


## Optional local computer vision (new in PR #163, no clicks)

The current BabyAI native text brain cannot analyze screenshot pixels.
The already existing screen.capture tool saves BMP observations on Windows,
behind user-controlled screen-capture permission.

Added ginger_local_vision.LocalGingerVision and trusted desktop command
vision.analyze.local. They send a previously captured BMP to a SEPARATE local
multimodal model, never to a remote/cloud host. The model returns a validated
object containing supplier-row title, starred search words, visible catalogue
candidates, status and warnings. Executable action fields are rejected.

This feature can use only 127.0.0.1:8080 or another explicitly supplied
numeric loopback port in range 1024..65535. It disables proxies and redirects.
No catalogue linking, editing, clicks or SQL access exist in this code.

### Local model installation option

llama.cpp's llama-server supports OpenAI-compatible base64/BMP multimodal
requests. An example model for initial Windows experiments is a small
Qwen2.5-VL 3B instruct quantized GGUF with multimodal projection support.
With suitable llama.cpp executable, run:

~~~powershell
llama-server.exe -hf ggml-org/Qwen2.5-VL-3B-Instruct-GGUF:Q4_K_M --alias local-vision --host 127.0.0.1 --port 8080 -ngl 20
~~~

This downloads model files separately. BabyAI does not install the model
or start llama.cpp automatically. On a GPU with 8GB VRAM, tune the number
of GPU layers down if another model is using video memory. Do not start
a cloud provider for business ERP screenshots. This setup is an example,
not a verified working install on the user's Windows PC.

### Trusted bridge command

After the existing permissioned screen.capture generated an observation ID,
submit its exact ID to the desktop bridge:

~~~json
{
  "command": "vision.analyze.local",
  "payload": {"observation_id": "paste-32-character-capture-id", "port": 8080}
}
~~~

It returns a result whose vision.analysis_status is local_vision_read_only
and vision.automated_linking is false. This is a trusted desktop command,
not a free-form model-executable computer-action tool.

### Full Ginger autonomy remains future work

Perception is only one component. A supervised GUI driver must be created,
with narrowly scoped keyboard/mouse primitives, process/window checks,
snapshot-before-and-after validation, and journaling. Linking an existing
product requires verifying actual product identity (brand, variant, type,
volume and exact selected Ginger row), not only visual name similarity.
Creating a NEW Ginger product remains separately approval-gated.
