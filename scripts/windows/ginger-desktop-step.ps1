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
 [GingerMenuAccess]::RequireSameProductRows($rows,[GingerMenuAccess]::ReadProductRows($window,$ownerId),$true)
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
  $expected=Get-Content -LiteralPath (Join-Path $ExpectedDirectory 'search-context.json') -Raw -Encoding UTF8 | ConvertFrom-Json
  [void][GingerMenuAccess]::SetSearchWord($window,$ownerId,[GingerCandidateSnapshot]$expected.SearchWords,$Word,[bool]$Enabled,$true)
 }
 Start-Sleep -Milliseconds 600
}
# Observe already has a full double-read snapshot and has issued no input since it.
# Navigation/filter operations must obtain a new snapshot after their input.
if($Operation -ne 'Observe'){Write-ReadStage 'table_after_input';$rows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)}
$searchDeadline=[Diagnostics.Stopwatch]::StartNew()
do {
 Write-ReadStage 'search_first'
 $first=[GingerMenuAccess]::ReadSearchContext($window,$ownerId)
 Start-Sleep -Milliseconds 700
 Write-ReadStage 'search_second'
 $second=[GingerMenuAccess]::ReadSearchContext($window,$ownerId)
 Write-ReadStage 'table_recheck'
 [GingerMenuAccess]::RequireSameProductRows($rows,[GingerMenuAccess]::ReadProductRows($window,$ownerId),$true)
 if($first.QueryWordsMatchSource -and $second.QueryWordsMatchSource) {
  [GingerMenuAccess]::RequireSameSearchContext($first,$second)
  break
 }
 # A stable but stale panel is not completion. Only repeat observation;
 # never replay Select/Filter or accept candidates belonging to another source.
 if($searchDeadline.Elapsed.TotalSeconds -ge 10){throw 'Search query differs from source after bounded observation; no input retry'}
} while($true)
Write-ReadStage 'verified'
[pscustomobject]@{process_id=$ownerId;rows=$rows} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'before.json') -Encoding UTF8
$second | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'search-context.json') -Encoding UTF8
[pscustomobject]@{status='observed';directory=$OutputDirectory;source=$second.Products.Source;candidates=@($second.Products.Candidates);words=@($second.SearchWords.Candidates);row_count=$rows.Length;process_id=$ownerId;search_completion_verified=$false} | ConvertTo-Json -Depth 8
