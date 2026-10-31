# Explicit development controller. Navigation only; not an Agent Runtime tool.
[CmdletBinding()]
param(
    [ValidateSet('Initialize','Step','Status')][string]$Operation='Status',
    [string]$QueueDirectory=(Join-Path $env:LOCALAPPDATA 'BabyAI/ginger-navigation/current'),
    [switch]$ConfirmNavigation
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$python=(Get-Command python.exe -ErrorAction Stop).Source
$coreSource=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../src'))
if(-not (Test-Path -LiteralPath (Join-Path $coreSource 'babyai/ginger_price_queue.py'))) {
    throw 'Queue controller requires the matching source checkout'
}
function Invoke-Queue([string]$Action,[string]$ReportPath='',[string]$Attempt='') {
    $entrypoint=Join-Path $PSScriptRoot 'ginger-queue-cli.py'
    $arguments=@('-X','utf8',$entrypoint,$Action,'--directory',$QueueDirectory)
    if($ReportPath){$arguments+=@('--report',$ReportPath)}
    if($Attempt){$arguments+=@('--attempt',$Attempt)}
    $raw=& $python @arguments
    if($LASTEXITCODE -ne 0){throw 'Queue operation failed; no automatic retry'}
    return (($raw -join "`n") | ConvertFrom-Json)
}
if($Operation -eq 'Status') {
    Invoke-Queue 'status' | ConvertTo-Json -Depth 6
    return
}
if($Operation -eq 'Step' -and -not $ConfirmNavigation){throw 'Navigation confirmation required'}
# Check unresolved intent BEFORE activating, selecting or opening anything.
if($Operation -eq 'Step') {
    $status=Invoke-Queue 'status'
    if($null -ne $status.pending_attempt){throw 'Previous navigation unresolved; no input permitted'}
}
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ginger-navigation.ps1'))
Invoke-Expression $source.Substring(0,$source.IndexOf('$processes = @('))
$targets=@(Get-Process Ginger4 -ErrorAction Stop | Where-Object MainWindowHandle -ne 0)
if($targets.Count -ne 1 -or [IO.Path]::GetFileName($targets[0].Path) -ine 'Ginger4.exe') {
    throw 'Exactly one visible Ginger executable required'
}
$window=$targets[0].MainWindowHandle
$ownerId=[uint32]$targets[0].Id
# A fresh list must already be visible. This controller never guesses a return action.
$prices=[GingerMenuAccess]::ReadPrices($window,$ownerId)
$report=[pscustomobject]@{process_id=$ownerId; catalogue_changed=$false; row_identity_verified=$false; prices=$prices}
$diagnostics=Join-Path $env:LOCALAPPDATA ('BabyAI/diagnostics/ginger-queue-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($diagnostics)
$reportPath=Join-Path $diagnostics 'prices.json'
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
if($Operation -eq 'Initialize') {
    Invoke-Queue 'create' $reportPath | ConvertTo-Json -Depth 6
    return
}
$next=Invoke-Queue 'next' $reportPath
if($null -eq $next.next_document) {
    Invoke-Queue 'status' | ConvertTo-Json -Depth 6
    return
}
# A concurrent controller may consume the planned item. begin() is exclusive and
# its returned price, not a cached preview, is the sole target of this step.
$intent=Invoke-Queue 'begin' $reportPath
$number=[string]$intent.document.number
$selected=[GingerMenuAccess]::SelectPrice($window,$ownerId,$prices,$number,$true)
[GingerMenuAccess]::OpenSelectedPrice($window,$ownerId,$selected,$number,$true)
$deadline=[Diagnostics.Stopwatch]::StartNew()
$document=$null
while($deadline.Elapsed.TotalSeconds -lt 10) {
    try {$document=[GingerMenuAccess]::ReadPriceDocument($window,$ownerId)} catch { $document=$null }
    if($null -ne $document){break}
    Start-Sleep -Milliseconds 200
}
if($null -eq $document){throw 'Opened document not verified; pending queue receipt retained'}
$documentPath=Join-Path $diagnostics 'opened-document.json'
$document | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $documentPath -Encoding UTF8
$result=Invoke-Queue 'observe' $documentPath ([string]$intent.attempt)
[pscustomobject]@{status=$result; diagnostics=$diagnostics; products_processed=$false; can_write=$false} | ConvertTo-Json -Depth 6
