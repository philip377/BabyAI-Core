# Bundled desktop controller. Every transition consumes an explicit, fresh observation.
[CmdletBinding()]
param(
 [ValidateSet('Observe','Prepare','Select','Filter')][string]$Operation='Observe',
 [Parameter(Mandatory=$true)][string]$OutputDirectory,
 [string]$ExpectedDirectory,
 [int]$Position=0,
 [string]$Word,
 [switch]$Enabled,
 [switch]$ConfirmNavigation
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ginger-navigation.ps1'))
& ([scriptblock]::Create($source.Substring(0,$source.IndexOf('$processes = @('))))
$p=@(Get-Process Ginger4 -ErrorAction Stop | Where-Object MainWindowHandle -ne 0)
if($p.Count -ne 1 -or [IO.Path]::GetFileName($p[0].Path) -ine 'Ginger4.exe'){throw 'Exactly one Ginger process required'}
$window=$p[0].MainWindowHandle
$ownerId=[uint32]$p[0].Id
if(Test-Path -LiteralPath $OutputDirectory){throw 'Output exists; input retry prohibited'}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$readClock=[Diagnostics.Stopwatch]::StartNew()
function Write-ReadStage([string]$Stage) {
 [pscustomobject]@{stage=$Stage;elapsed_ms=$readClock.ElapsedMilliseconds;time_utc=[DateTime]::UtcNow.ToString('o')} |
  ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $OutputDirectory 'read-progress.jsonl') -Encoding UTF8
}
Write-ReadStage 'activate'
[GingerMenuAccess]::ActivateForRead($window,$ownerId)
Write-ReadStage 'initial_table'
$rows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)
if($Operation -eq 'Prepare') {
 if(-not $ConfirmNavigation){throw 'Navigation confirmation required'}
 $document=[GingerMenuAccess]::ReadPriceDocument($window,$ownerId)
 [pscustomobject]@{operation='Prepare';document=$document;rows=$rows;time_utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'navigation-intent.json') -Encoding UTF8
 [GingerMenuAccess]::OpenDocumentMatching($window,$ownerId,$document,$true)
 $deadline=[Diagnostics.Stopwatch]::StartNew()
 do {
  try {
   $view=[GingerMenuAccess]::InspectMatching($window,$ownerId)
  } catch {
   if($_.Exception.GetBaseException() -isnot [GingerUiBusyException]){throw}
   if($deadline.Elapsed.TotalSeconds -ge 10){throw}
   Start-Sleep -Milliseconds 200
   continue # Loading is observation-only; do not repeat navigation.
  }
  if($view.Status -ne 'panels_not_verified'){break}
  if($deadline.Elapsed.TotalSeconds -ge 10){throw 'Matching panels not verified after navigation; no retry'}
  Start-Sleep -Milliseconds 200
 } while($true)
 $afterDocument=[GingerMenuAccess]::ReadPriceDocument($window,$ownerId)
 if($afterDocument.Number -ne $document.Number -or $afterDocument.Supplier -ne $document.Supplier){throw 'Document changed during preparation'}
 Write-ReadStage 'prepared_table'
 $preparedRows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)
 [GingerMenuAccess]::RequireSameProductRows($rows,$preparedRows,$true)
 # This is already the fresh, double-read snapshot after preparation. No input
 # occurs before the search-context read, so a second immediate copy adds delay
 # without crossing another input boundary. The final table recheck stays below.
 $rows=$preparedRows
}
if($Operation -in @('Select','Filter')) {
 if(-not $ConfirmNavigation -or -not $ExpectedDirectory){throw 'Confirmed observation required'}
 $before=Get-Content -LiteralPath (Join-Path $ExpectedDirectory 'before.json') -Raw -Encoding UTF8 | ConvertFrom-Json
 if($before.process_id -ne $ownerId){throw 'Process changed'}
 [GingerMenuAccess]::RequireSameProductRows([GingerProductRow[]]$before.rows,$rows,$true)
 [pscustomobject]@{operation=$Operation;position=$Position;word=$Word;enabled=[bool]$Enabled;time_utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'intent.json') -Encoding UTF8
 if($Operation -eq 'Select') {
  [void][GingerMenuAccess]::RestoreProductSelection($window,$ownerId,$rows,$Position,$true)
 } else {
  $searchPath=Join-Path $ExpectedDirectory 'search-context.json'
  $filterPath=Join-Path $ExpectedDirectory 'filter-context.json'
  if((Test-Path -LiteralPath $searchPath) -and (Test-Path -LiteralPath $filterPath)){throw 'Ambiguous search observation'}
  $contextPath=if(Test-Path -LiteralPath $filterPath){$filterPath}else{$searchPath}
  $expected=Get-Content -LiteralPath $contextPath -Raw -Encoding UTF8 | ConvertFrom-Json
  [void][GingerMenuAccess]::SetSearchWord($window,$ownerId,[GingerCandidateSnapshot]$expected.SearchWords,$Word,[bool]$Enabled,$true)
 }
 Start-Sleep -Milliseconds 600
}
# Observe already has a full double-read snapshot and has issued no input since it.
# Navigation/filter operations must obtain a new snapshot after their input.
if($Operation -in @('Select','Filter')){Write-ReadStage 'table_after_input';$rows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)}
$searchDeadline=[Diagnostics.Stopwatch]::StartNew()
$busyObservationRetried=$false
$pendingObservationRetried=$false
do {
 Write-ReadStage 'search_pair'
 # A product overflow permits only a separate, verified filter-only context.
 # It never represents an empty/truncated candidate list and cannot authorize a match.
 $filterOnly=$false
 try {
  try {
   $second=[GingerMenuAccess]::ReadSearchContext($window,$ownerId,700)
  } catch {
   if($_.Exception.GetBaseException() -isnot [GingerCandidateLimitException]){throw}
   Write-ReadStage 'search_filters_only'
   $second=[GingerMenuAccess]::ReadFilterOnlyContext($window,$ownerId,700)
   $filterOnly=$true
  }
 } catch {
  if($_.Exception.GetBaseException() -is [GingerSearchPendingException]) {
   # Discard the partial context. A stale query or a newly bounded product list
   # permits another observation only, never another Select/Filter/Prepare input.
   Write-ReadStage 'table_recheck'
   [GingerMenuAccess]::RequireSameProductRows($rows,[GingerMenuAccess]::ReadProductRows($window,$ownerId),$true)
   # A full read on a large price can already take over ten seconds. Permit
   # exactly one fresh read rather than expiring before the first retry. The
   # desktop session's 120-second process deadline still bounds the operation.
   if($pendingObservationRetried){throw 'Ginger search did not settle after one additional observation; no input retry'}
   $pendingObservationRetried=$true
   Write-ReadStage 'search_pending'
   Start-Sleep -Milliseconds 200
   continue
  }
  if($_.Exception.GetBaseException() -isnot [GingerUiBusyException]){throw}
  if($busyObservationRetried -or $searchDeadline.Elapsed.TotalSeconds -ge 10){throw}
  $busyObservationRetried=$true
  Write-ReadStage 'search_busy'
  Start-Sleep -Milliseconds 200
  # Discard the incomplete context. Repeat reads only, once; the original table
  # and selection must still pass the full comparison before anything is accepted.
  continue
 }
 Write-ReadStage 'table_recheck'
 [GingerMenuAccess]::RequireSameProductRows($rows,[GingerMenuAccess]::ReadProductRows($window,$ownerId),$true)
 if($second.QueryWordsMatchSource) {
  break
 }
 # A stable but stale panel is not completion. Only repeat observation;
 # never replay Select/Filter or accept candidates belonging to another source.
 if($searchDeadline.Elapsed.TotalSeconds -ge 10){throw 'Search query differs from source after bounded observation; no input retry'}
} while($true)
Write-ReadStage 'verified'
[pscustomobject]@{process_id=$ownerId;rows=$rows} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'before.json') -Encoding UTF8
if($filterOnly) {
 $second | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'filter-context.json') -Encoding UTF8
} else {
 $second | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'search-context.json') -Encoding UTF8
}
# Emit a single explicitly typed observation; filter-only has no candidates field.
if($filterOnly) {
 [pscustomobject]@{status='needs_filters';directory=$OutputDirectory;source=$second.SearchWords.Source;candidate_count=$second.ProductRows;words=@($second.SearchWords.Candidates);row_count=$rows.Length;process_id=$ownerId;search_completion_verified=$false} | ConvertTo-Json -Depth 8
} else {
 [pscustomobject]@{status='observed';directory=$OutputDirectory;source=$second.Products.Source;candidates=@($second.Products.Candidates);words=@($second.SearchWords.Candidates);row_count=$rows.Length;process_id=$ownerId;search_completion_verified=$false} | ConvertTo-Json -Depth 8
}
