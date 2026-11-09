# One explicit pair, durable receipt, no retries and no document close/save.
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$ObservationDirectory,
 [Parameter(Mandatory=$true)][string]$ReceiptDirectory,
 [Parameter(Mandatory=$true)][string]$CandidateTitle,
 [switch]$ConfirmMatch
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ginger-navigation.ps1'))
& ([scriptblock]::Create($source.Substring(0,$source.IndexOf('$processes = @('))))
$expectedRows=Get-Content (Join-Path $ObservationDirectory 'before.json') -Raw | ConvertFrom-Json
$expectedContext=Get-Content (Join-Path $ObservationDirectory 'search-context.json') -Raw | ConvertFrom-Json
$targets=@(Get-Process Ginger4 -ErrorAction Stop | Where-Object MainWindowHandle -ne 0)
if($targets.Count -ne 1 -or $targets[0].Id -ne $expectedRows.process_id){throw 'Observed process changed'}
$window=$targets[0].MainWindowHandle
$ownerId=[uint32]$targets[0].Id
if(Test-Path -LiteralPath $ReceiptDirectory){throw 'Receipt already exists; automatic retry forbidden'}
[void][IO.Directory]::CreateDirectory($ReceiptDirectory)
# Exclusive reservation: even an uncertain failed invocation consumes this receipt.
$reservation=[IO.File]::Open((Join-Path $ReceiptDirectory 'reservation.lock'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try {
 [GingerMenuAccess]::ActivateForRead($window,$ownerId)
 $rows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)
 [GingerMenuAccess]::RequireSameProductRows([GingerProductRow[]]$expectedRows.rows,$rows,$true)
 if($ConfirmMatch) {
  $selected=@($rows | Where-Object Selected)
  if($selected.Count -ne 1){throw 'Exactly one source row required'}
  $sourcePosition=$selected[0].Observation.ObservedRowPosition
  [pscustomobject]@{position=$sourcePosition;action='focus_source_only'} | ConvertTo-Json | Set-Content (Join-Path $ReceiptDirectory 'selection-intent.json') -Encoding UTF8
  [void][GingerMenuAccess]::RestoreProductSelection($window,$ownerId,$rows,$sourcePosition,$true)
  $rows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)
  [GingerMenuAccess]::RequireActiveBlueSource($window,$ownerId,$rows,$sourcePosition)
 }
 $context=[GingerMenuAccess]::ReadSearchContext($window,$ownerId)
 if(-not $context.QueryWordsMatchSource){throw 'Search query does not describe selected source'}
 [GingerMenuAccess]::RequireSameCandidates([GingerCandidateSnapshot]$expectedContext.Products,$context.Products)
 $pair=@($context.Products.Candidates | Where-Object Title -CEQ $CandidateTitle)
 if($pair.Count -ne 1){throw 'Exact unique candidate required'}
 $position=$context.Products.Source.ObservedRowPosition
 [pscustomobject]@{rows=$rows;context=$context;process_id=$ownerId} | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $ReceiptDirectory 'before.json') -Encoding UTF8
 if(-not $ConfirmMatch){
  [pscustomobject]@{status='prepared_only';source_position=$position;candidate=$CandidateTitle;input_sent=$false} | ConvertTo-Json
  return
 }
 # Stable per-source claim, independent of output directory or candidate choice.
 # No automatic recovery: a crash after reservation requires explicit reconciliation.
 $claimRoot=Join-Path $env:LOCALAPPDATA 'BabyAI/ginger-matching-claims'
 [void][IO.Directory]::CreateDirectory($claimRoot)
 $identity=@($ownerId,$context.Products.Source.TableHandle,$context.Products.Source.DocumentNumber,$context.Products.Source.Supplier,$position,$context.Products.Source.Title) | ConvertTo-Json -Compress
 $sha=[Security.Cryptography.SHA256]::Create()
 try {$key=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($identity)))).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
 $claimPath=Join-Path $claimRoot ($key+'.json')
 $claim=[IO.File]::Open($claimPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try {
  $payload=[Text.Encoding]::UTF8.GetBytes(([pscustomobject]@{identity=$identity;receipt=$ReceiptDirectory;candidate=$CandidateTitle;status='reserved_no_retry'} | ConvertTo-Json))
  $claim.Write($payload,0,$payload.Length)
  $claim.Flush($true)
 } finally {$claim.Dispose()}
 [pscustomobject]@{source=$context.Products.Source;candidate=$CandidateTitle;time_utc=[DateTime]::UtcNow.ToString('o');authorization='Explicit desktop confirmation for this pair or this authorized pass';status='dispatch_intent_no_retry'} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $ReceiptDirectory 'intent.json') -Encoding UTF8
 [GingerMenuAccess]::InvokeMatchingCandidate($window,$ownerId,$rows,$context.Products,$pair[0].ObservedPosition,$CandidateTitle,$true)
 Start-Sleep -Milliseconds 600
 $after=$null
 $lastReadError=$null
 for($readAttempt=0;$readAttempt -lt 8;$readAttempt++) {
  try {
   $observed=[GingerMenuAccess]::ReadProductRows($window,$ownerId)
   if($observed.Length -eq $rows.Length -and $observed[$position-1].Observation.LinkedProductText -ceq $CandidateTitle){$after=$observed;break}
  } catch {$lastReadError=$_}
  Start-Sleep -Milliseconds 500
 }
 if($null -eq $after){throw 'Matching dispatched once; result unresolved. Read-only reconciliation required; do not repeat.'}
 $after | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $ReceiptDirectory 'after.json') -Encoding UTF8
 if($rows.Length -ne $after.Length){throw 'Post-action table size changed; outcome requires inspection'}
 for($i=0;$i -lt $rows.Length;$i++) {
  $old=$rows[$i].Observation
  $new=$after[$i].Observation
  foreach($field in @('TableHandle','ObservedRowPosition','DocumentNumber','Supplier','Title','PriceText')) {
   if($old.$field -cne $new.$field){throw "Post-action source/table changed: $field"}
  }
  if($old.ObservedRowPosition -eq $position) {
   if($new.LinkedProductText -cne $CandidateTitle){throw 'Matching result not exact; do not repeat action'}
  } elseif($old.LinkedProductText -cne $new.LinkedProductText){throw 'Another product link changed; inspect manually'}
 }
 $verified=[pscustomobject]@{status='matched_verified';source_position=$position;candidate=$CandidateTitle;verified_in_ginger=$true;document_closed=$false}
 $verified | ConvertTo-Json | Set-Content (Join-Path $ReceiptDirectory 'result.json') -Encoding UTF8
 $verified | ConvertTo-Json
} catch {
 ($_ | Out-String) | Set-Content (Join-Path $ReceiptDirectory 'failure.txt') -Encoding UTF8
 throw
} finally {$reservation.Dispose()}
