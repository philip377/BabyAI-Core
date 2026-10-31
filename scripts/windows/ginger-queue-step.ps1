# Explicit development controller. Navigation only; not an Agent Runtime tool.
[CmdletBinding()]
param(
    [ValidateSet('Initialize','Step','ReadCurrent','Status')][string]$Operation='Status',
    [string]$QueueDirectory=(Join-Path $env:LOCALAPPDATA 'BabyAI/ginger-navigation/current'),
    [switch]$ConfirmNavigation
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
trap {
    # Local troubleshooting only; do not put raw exceptions into the user's journal.
    if((Get-Variable diagnostics -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $diagnostics)) {
        ($_ | Out-String) | Set-Content -LiteralPath (Join-Path $diagnostics 'failure.txt') -Encoding UTF8
    }
    break
}
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$python=(Get-Command python.exe -ErrorAction Stop).Source
$coreSource=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../src'))
if(-not (Test-Path -LiteralPath (Join-Path $coreSource 'babyai/ginger_price_queue.py'))) {
    throw 'Queue controller requires the matching source checkout'
}
function Invoke-Queue([string]$Action,[string]$ReportPath='',[string]$Attempt='') {
    $entrypoint=Join-Path $PSScriptRoot 'ginger-queue-cli.py'
    $arguments=@('-X','utf8',$entrypoint,$Action,'--directory',$QueueDirectory)
    if($Action -eq 'create'){$arguments+='--require-read'}
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
if($Operation -in @('Step','ReadCurrent') -and -not $ConfirmNavigation){throw 'Navigation confirmation required'}
# Check unresolved intent BEFORE activating, selecting or opening anything.
if($Operation -in @('Step','ReadCurrent')) {
    $status=Invoke-Queue 'status'
    if($Operation -eq 'Step' -and $null -ne $status.pending_attempt){throw 'Previous navigation unresolved; no input permitted'}
    if($Operation -eq 'Step' -and $status.navigation_exhausted){$status | ConvertTo-Json -Depth 6; return}
    if($Operation -eq 'ReadCurrent' -and -not $status.awaiting_read){throw 'No opened document awaiting reading'}
}
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ginger-navigation.ps1'))
Invoke-Expression $source.Substring(0,$source.IndexOf('$processes = @('))
$targets=@(Get-Process Ginger4 -ErrorAction Stop | Where-Object MainWindowHandle -ne 0)
if($targets.Count -ne 1 -or [IO.Path]::GetFileName($targets[0].Path) -ine 'Ginger4.exe') {
    throw 'Exactly one visible Ginger executable required'
}
$window=$targets[0].MainWindowHandle
$ownerId=[uint32]$targets[0].Id
function Read-QueuedDocument {
    $state=Invoke-Queue 'status'
    if(-not $state.awaiting_read -or -not $state.requires_read){throw 'No pending document reading'}
    $current=[GingerMenuAccess]::ReadPriceDocument($window,$ownerId)
    if($current.Number -cne $state.pending_document.number -or $current.Supplier -cne $state.pending_document.supplier) {
        throw 'Current document does not match pending reading; no tab action permitted'
    }
    $tabScript=Join-Path $PSScriptRoot 'ginger-new-matching-tab.ps1'
    $powershell=Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $tabRaw=& $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $tabScript -ConfirmNavigation 2> (Join-Path $diagnostics 'tab-error.txt')
    if($LASTEXITCODE -ne 0){throw 'New matching tab not verified; pending reading retained'}
    $tab=($tabRaw -join "`n") | ConvertFrom-Json
    $tab | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $diagnostics 'tab-result.json') -Encoding UTF8
    if($tab.status -ne 'new_matching_tab_visually_selected' -or $tab.document_number_and_supplier_stable -ne $true) {
        throw 'Unexpected tab result; pending reading retained'
    }
    $current=[GingerMenuAccess]::ReadPriceDocument($window,$ownerId)
    if($current.Number -cne $state.pending_document.number -or $current.Supplier -cne $state.pending_document.supplier) {
        throw 'Document changed after tab navigation'
    }
    [GingerMenuAccess]::OpenDocumentMatching($window,$ownerId,$current,$true)
    $snapshot=[GingerMenuAccess]::ReadMatchingCandidates($window,$ownerId)
    $readingPath=Join-Path $diagnostics 'selected-position-and-candidates.json'
    $snapshot | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $readingPath -Encoding UTF8
    return (Invoke-Queue 'read' $readingPath ([string]$state.pending_attempt))
}
$diagnostics=Join-Path $env:LOCALAPPDATA ('BabyAI/diagnostics/ginger-queue-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($diagnostics)
if($Operation -eq 'ReadCurrent') {
    $result=Read-QueuedDocument
    [pscustomobject]@{status=$result; diagnostics=$diagnostics; products_processed=$false; can_write=$false} | ConvertTo-Json -Depth 6
    return
}
# Use the existing guarded semantic menu driver, never a guessed Back/Close click.
# Pending intent and exhausted-plan guards above run before any UI access.
if($Operation -eq 'Step' -and -not [GingerMenuAccess]::PricesVisible($window,$ownerId)) {
    $powershell=Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $navigation=Join-Path $PSScriptRoot 'ginger-navigation.ps1'
    $returnRaw=& $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $navigation -Mode OpenPrices -ConfirmNavigation 2> (Join-Path $diagnostics 'return-error.txt')
    if($LASTEXITCODE -ne 0){throw 'Price list return failed; no automatic retry'}
    $returned=($returnRaw -join "`n") | ConvertFrom-Json
    $returned | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $diagnostics 'return-to-prices.json') -Encoding UTF8
    if($returned.process_id -ne $ownerId -or $returned.prices_visible -ne $true -or $returned.catalogue_changed -ne $false) {
        throw 'Price list return not verified'
    }
}
# Re-read the complete list and validate it against the durable plan before selecting.
$prices=[GingerMenuAccess]::ReadPrices($window,$ownerId)
$report=[pscustomobject]@{process_id=$ownerId; catalogue_changed=$false; row_identity_verified=$false; prices=$prices}

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
if($result.requires_read){$result=Read-QueuedDocument}
[pscustomobject]@{status=$result; diagnostics=$diagnostics; products_processed=$false; can_write=$false} | ConvertTo-Json -Depth 6
