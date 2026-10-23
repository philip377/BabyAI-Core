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


## 23 October: one-command UNIX visual pilot (read-only, not yet acceptance-tested)

The pilot is now connected to the *existing* Windows screen-capture permission
and to UNIX's trusted desktop bridge. It is also included as the optional
read-only tool in the conversational AgentExecutor catalog:

- "ginger.observe" (by chat: "посмотри Ginger").
- Required capability: SCREEN_CAPTURE, handled by the existing permission
  approval flow; the pilot never grants permission itself.
- First inspect the active HWND's actual process executable path:
  only an active process named Ginger4.exe is accepted, not a spoofed title.
- Take a *fresh* active-window BMP with the existing ScreenCaptureStore.
- Check the same Ginger foreground handle still owns focus after capture.
  On focus change, discard the unexpected capture and stop.
- Pass the BMP to the already implemented local-only VLM HTTP endpoint at
  127.0.0.1:8080 (or explicitly selected loopback port).
- Return source title, starred words, catalogue candidates, and a read-only
  recommended next step.
- A visible source INTENSE, QAHWA, ELIXIR, EXTREME or ABSOLU is protected:
  if none of the shown catalogue titles contains that variant, the result
  becomes "template_discovery_only", NOT an existing-product match.
- A missing candidate remains "research_missing" (not automatically
  authorization to create a SKU). Unreadable or contradictory screens pause.

No mouse/keyboard clicks, no Ginger writes, no SQL access, no automated product
linking, and no model-driven action coordinates are included.

### Manual installation and first test

1. Download trusted Windows llama.cpp binaries which contain llama-server.exe.
   Model weights are not included in the BabyAI installer.
2. Download scripts/windows/start-ginger-vision.ps1 from this PR branch.
   Launch it from a PowerShell window:

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\start-ginger-vision.ps1 -LlamaServer "C:\path\to\llama-server.exe" -Port 8080 -GpuLayers 12
~~~

The script binds only 127.0.0.1, uses model repo
ggml-org/Qwen2.5-VL-3B-Instruct-GGUF:Q4_K_M and alias local-vision.
The first run may download several GB of model and projector weights.
VRAM usage is unmeasured on the user's computer; if it doesn't fit, lower
GpuLayers or try -CpuVisionProjector. Keep the separate console open for the
pilot test and press Ctrl+C to stop.

3. Open Ginger4 and explicitly select the SUPPLIER PRICE-LIST MATCHING window.
   The active foreground application must be Ginger4.exe.
4. Start UNIX/BabyAI with this branch. In chat, ask "посмотри Ginger",
   then approve its normal screen-capture permission request.
5. Alternatively, for trusted local CLI testing with a deliberately granted
   screen capability, use:

~~~powershell
babyai permissions grant screen.capture
babyai-desktop exec ginger.observe
~~~

   Revoke afterwards if the user does not want persistent capture access:

~~~powershell
babyai permissions revoke screen.capture
~~~

6. Verify that returned supplier text and the two bottom tables match the
   screen and that the model did NOT suggest merging a variant with a base.
   This command is currently a smoke test; it cannot click stars or link
   products. Capture and visual processing may take time on an 8GB GPU.

### Next implementation

Build verified click/keyboard primitives *scoped to Ginger4 only* and an
action journal. First supervised search-only star toggles with fresh visual
verification, then linking with strict product ID/variant checks. New product
creation stays behind an additional approval gate. The native text brain must
never silently approve vision-only source-to-SKU mappings.


## CPU-only acceptance test on low-power Windows workstations

A separate self-contained PowerShell script provides a real read-only
Ginger OCR test without installing the UNIX desktop release or using GPU:
scripts/windows/test-ginger-cpu-ocr.ps1.

Dependencies: Tesseract OCR local Windows executable and English and
(recommended) Russian OCR language data. The script does not download models
or software. It does not touch the Ginger SQL Server.

Install the OCR engine with explicit owner action if it is missing:

~~~powershell
winget install -e --id UB-Mannheim.TesseractOCR
~~~

Include eng and rus training data during Windows installation. The script
searches PATH and the standard Program Files install location. Without rus,
the script records a warning and Cyrillic may be unreadable.

Open Ginger4 on the SUPPLIER PRICE LIST matching page and download the script
via GitHub Raw into Downloads. Run:

~~~powershell
cd "$env:USERPROFILE\Downloads"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\test-ginger-cpu-ocr.ps1" -SupplierTitle "Bruno Banani Man Intense edP 30ml"
~~~

The optional SupplierTitle flags absent variant tokens such as INTENSE and
QAHWA in the candidate panel, but NEVER authorizes a mapping.

The script verifies the real foreground process is Ginger4, enumerates native
SysListView32 child windows (up to 30000 enumeration attempts), selects the
two largest lower listviews, and crops only their on-screen rectangles.
If the tables are not discoverable, it uses an approximate bottom window
crop and flags this explicitly.

It uses local Tesseract to OCR the PNG crops and creates a timestamped folder
in Downloads with ginger-cpu-report.json, matching-left.png,
catalogue-right.png and OCR text files. The JSON shows readable lines from
both tables, chosen OCR language, warnings, and read-only flags. Nothing is
clicked or written in Ginger. No network request is made.

Review JSON for business-sensitive information before sharing it. No files
or screenshots are uploaded automatically. A Windows PowerShell 5.1 syntax
CI workflow checks parseability of the standalone test script.

Next acceptance gate: compare recognized titles against the same screen.
Only then build a strictly scoped and approval-gated click/verify loop.


### Windows Terminal foreground regression (October 23, 2026)

First on-machine attempt confirmed that Tesseract was installed, but the
CPU OCR script exited before capture: the PowerShell process was the
foreground WindowsTerminal, not Ginger4. The failure was in the script's
foreground assumption, not Tesseract, Ginger, or OCR quality.

The updated test-ginger-cpu-ocr.ps1 enumerates actual running Ginger4
processes, requires one visible main window, restores/activates that window
with Win32 ShowWindow/SetForegroundWindow, and verifies that Ginger4 is
really foreground. If Windows denies programmatic focus, it gives the
operator four seconds to click Ginger. It does not click controls or
modify business data. The subsequent capture still checks foreground
identity and aborts if the window changes.

Replace the old downloaded .ps1 with the latest GitHub Raw before rerunning.
Tesseract does not need reinstalling. If the foreground issue recurs,
check that Ginger4 is open and the supplier matching form is visible, then
report the new console error.


### Real CPU OCR result and Russian training data improvement

The owner ran the CPU OCR script on Ginger4 (23 October 2026) and returned
ginger-cpu-report.json. It confirmed:

- capture_mode = native_listview_regions; native_listviews_found = 2.
- local_only, read_only, ginger_modified = false.
- Tesseract language = eng only; Russian model unavailable.
- Right catalogue text partially readable, e.g. Bois 1920 ORO ROSSO
  edp 100ml and a tester row; Cyrillic and numeric suffixes inaccurate.
- Left search-word area contained badly garbled Cyrillic.
- No automatic match should be attempted from these OCR strings alone.

To remove the blocker, scripts/windows/setup-ginger-russian-ocr.ps1 now downloads
the Russian rus.traineddata model from the official tesseract-ocr/tessdata_fast
repository. It copies the installed eng.traineddata into a PER-USER folder under
LOCALAPPDATA/BabyAI/Tesseract/tessdata. No admin privileges, no change to
Program Files, and no calls to Ginger. Download is a separate, explicit owner
action, never implicit in the read-only OCR script.

The updated test-ginger-cpu-ocr.ps1 automatically discovers this folder and
passes --tessdata-dir to Tesseract. It also scales matching-pane crops by 2x
before OCR to assist recognition of small WinForms text. The OCR script itself
remains offline and read-only.

Run from a PowerShell console in Downloads after saving BOTH updated .ps1
files via GitHub Raw:

~~~powershell
cd "$env:USERPROFILE\Downloads"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\setup-ginger-russian-ocr.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\test-ginger-cpu-ocr.ps1"
~~~

Keep the supplier matching page open. Confirm the next report has
language = eng+rus and native_listviews_found = 2. Then manually inspect
the two locally saved crops and compare OCR strings to the actual Ginger
rows; no mapping can be authorized from approximate text.


### 23 October second real OCR acceptance result (eng+rus)

The owner submitted ginger-cpu-report(1).json after installing the per-user
Russian traineddata and running the new 2x OCR:
- engine: Tesseract CPU; language: eng+rus
- capture_mode: native_listview_regions; native_listviews_found: 2
- local_only: true; ginger_modified: false
- Lower-left words were generally legible: BOIS, 1920, ORO, ROSSO, edp, 100.
- Right titles included Bois 1920 ORO ROSSO edp 100ml and a tester,
  but OCR still confuses ORO vs Cyrillic OVO, "100ml" vs "100m!" and
  one other brand title.
- Warnings were empty in the original report only because the v1 warnings
  checked for missing languages/empty text; that is NOT an accuracy guarantee.

IMPORTANT: Two lower panes alone cannot tell the agent which TOP supplier
row is selected. Never infer source product identity from bottom search words.

The updated CPU OCR script now accepts an explicit opt-in switch:
-CaptureSupplierGrid. It captures a separate supplier-grid.png area between the
upper window chrome and the two lower panes and OCRs it with sparse-text PSM 11.
This extra scan may include price/commercial information from many supplier
rows. It remains entirely local; users should REVIEW before sharing any file.
The report adds supplier_grid_lines and selected_supplier_row_verified=false.
It also explicitly reports safe_for_automatic_matching=false and an
unverified-OCR warning, even when the reader detected nonempty text.

Example rerun:

~~~powershell
cd "$env:USERPROFILE\Downloads"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\test-ginger-cpu-ocr.ps1" -CaptureSupplierGrid
~~~

The next planned acceptance gate is detecting the selected/highlighted supplier
row with reliable UI evidence and verifying the line against a human-observed
screen. Do not use OCR-only text to double-click a proposed item or create SKU.


### Third on-machine CPU test: upper supplier list (23 October 2026)

Owner's ginger-cpu-report(2).json confirmed CPU OCR eng+rus, 2 native lower
listviews and a third captured supplier-grid area. Source rows such as
Bois 1920 Parana, Boss Hugo and Boss Bottled titles are legible, and many
candidate mappings in the column "Товар" show <не указано>. However,
Tesseract sparse-text PSM 11 splits the supplier grid's *different columns*
into separate lines and the first visible item starts as partial
"osso едp / 100:". Critically, selected_supplier_row_verified=false:
none of those lines is a confirmed selected supplier SKU.

The 23 October improvement moves the upper-grid crop start from 24% to
16% of the Ginger foreground window height (helps preserve the first
visible row), and adds separate Tesseract TSV output grouped by OCR lines,
with x/y/width/height and OCR confidence relative to the 2x grid PNG.
The JSON includes supplier_grid_segments and
supplier_grid_segment_coordinates="relative_to_2x_supplier_grid_png".

No selected row is inferred from OCR alone; screenshots may be sent for
visual calibration of the highlight background and grid columns.
Users can supply the ALREADY GENERATED prior
ginger-cpu-test-20261023-175618/supplier-grid.png for such visual
calibration without rerunning anything. Before sharing, review commercial
prices and customer/supplier information visible in the grid.

The new code is still read-only. Automatic mapping and Ginger interactions
remain disabled pending row-identity verification.


### Owner screenshot verifies first-column LightBlue selected cell

The source supplier-grid.png (2048x597 in the shared crop) confirms:
- the FIRST visible supplier row has a LightBlue background only in the
  NAME column, while other rows have pale-yellow backgrounds;
- the active row arrow is visible at the left;
- the first source row reads "Bois 1920 Oro Rosso edp 100 ml", price 69.30,
  and target column "<не указано>"; it is NOT yet linked.

Pixel inspection of the actual provided screenshot found an unambiguous
LightBlue horizontal stripe near y=60..75 in the screenshot crop, e.g.
RGB(173,216,230) in the name column. Name-grid background otherwise
appears pale yellow, e.g. RGB(255,255,225).

CPU OCR script now tests for a UNIQUE stripe in a left-name-column sampling
band; if none or multiple bands are found it declines to infer a selected row.
With one stripe, it exports selected-supplier-row.png and OCRs that small
crop separately (Tesseract PSM 7) into selected_row_visual_candidate.
It reports stripe y and height RELATIVE TO the 2x supplier-grid PNG.

Despite these advances, the auto-detected title is only visual evidence.
selected_supplier_row_verified remains FALSE and
safe_for_automatic_matching remains FALSE until the selected Ginger item
is tied to stable UI/ERP row identity. No controls are clicked or changed.
The PowerShell workflow includes synthetic LightBlue row detection tests.


### Actual OCR TSV export regression and fix (owner Windows screenshot)

When running the selected-supplier-row test, the owner repeatedly received:
  tesseract.exe: read_params_file: Can't open tsv
  scripts/windows/test-ginger-cpu-ocr.ps1 (Read-Grid-OcrSegments)

Root cause: earlier per-user OCR setup intentionally copies only
eng.traineddata and rus.traineddata into LOCALAPPDATA/BabyAI/Tesseract/tessdata.
Tesseract's trailing "tsv" argument is a reference to the separate file
tessdata/configs/tsv, which is absent in that per-user folder.
This is a SCRIPT BUG, not an installation failure.

The corrected script passes built-in Tesseract switches:
  -c tessedit_create_tsv=1 -c tessedit_create_txt=0
and does NOT pass "tsv" as an external config filename. The OCR engine writes
the same .tsv output containing word bounding boxes. Reproduced the failure
and verified this fix independently with Tesseract 5 and an isolated tessdata
directory containing only eng.traineddata.

No reinstall or new OCR model is needed: simply replace the downloaded
test-ginger-cpu-ocr.ps1 with the updated branch version and rerun
-CaptureSupplierGrid. The output remains read-only and the selected row's
business identity remains unverified; no automatic matching.


### Real screenshot / 4th report: false duplicate blue band (23 October)

The owner supplied supplier-grid(1).png and ginger-cpu-report(3).json.
Report correctly recognized the two matching controls and OCR coordinates,
but selected_row_color_band_detected=false and selected title=null, despite
a visibly selected FIRST supplier title row.

Root cause verified on the supplied screenshot pixel colors: the selected
name cell is light blue RGB approximately (173,216,230), but the dock
"Сопоставление с каталогом" at the bottom of the captured image also
contains a thin full-width band of blue gradients. The earlier algorithm
detected two distinct blue bands after upscaling and intentionally rejected
nonunique selections. This is NOT failure of Ginger selection or OCR.

Fix: require light blue within the LEFT name-column sample (8.5%..14.5%
of width), while disallowing matching blue across a RIGHT-column sample
(24.5%..30.5%) that falls outside the title cell. Also exclude the bottom
5% of the crop. A screenshot rescaled to the original OCR output dimensions
reproduced two bands under the old criteria (roughly y=372 and 1665) and
only one under the new criteria (roughly y=372, 37px tall).

Windows CI contains a synthetic graphical regression test with an actual
blue name-cell row and a full-width bottom dock, followed by a second
name-only blue band that must still be rejected as ambiguous. Do not
automatically link based on a detected blue band; supplier row remains an
unverified visual observation until its identity is corroborated.


### Fifth on-machine CPU report: selected band found, single-line OCR blank

User's ginger-cpu-report(5).json confirms:
- selected_row_color_band_detected=true;
- selected_row_band_y_in_2x_png=370, selected_row_band_height_in_2x_png=37;
- selected_row_image_local was created, but selected_row_visual_candidate=null;
- supplier row still not verified, and automatic linking remains disabled.

Root cause reproduced with the previous supplied supplier-grid(1).png,
rescaled 2x: the script's crop started 5 pixels ABOVE the light-blue
row and extended 5 pixels BELOW it. Even short neighboring-row fragments
cause Tesseract PSM 7 ("single line") to output empty text. A tight
crop INSIDE the blue stripe reproduced
"Bois 1920 Oro Rosso edp 100 mi" with Tesseract 5. The 100 mi
suffix remains an OCR error, so this is not an authoritative SKU match.

Fix: selected crop begins 4 pixels below the detected start and its height
equals stripe height minus 8 pixels, with bounds validation. The same
read-only OCR analysis returns the unverified visual title and an explicit
warning if it remains empty. On a different font scale the measured stripe
may differ; recheck the generated selected-supplier-row.png before adapting
the thresholds. No clicks, link actions, ERP writes or SQL access.
