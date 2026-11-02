# Explicit development controller: selection receipts only, no candidate freshness claim.
[CmdletBinding()]
param(
    [ValidateSet('Initialize','Step','Recover','Observe','Read','InspectSearch','InspectQuery','InspectSelection','PrepareFilters','FinishReview','NextReviewed','Status')][string]$Operation='Status',
    [Parameter(Mandatory=$true)][string]$QueueDirectory,
    [switch]$ConfirmNavigation,
    [string]$PreparationDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$python=(Get-Command python.exe -ErrorAction Stop).Source
function Invoke-Journal([string]$Action,[string]$ReportPath='',[string]$Attempt='') {
    $arguments=@('-X','utf8',(Join-Path $PSScriptRoot 'ginger-row-cli.py'),$Action,'--directory',$QueueDirectory)
    if($Action -eq 'create'){$arguments+='--require-read'}
    if($ReportPath){$arguments+=@('--report',$ReportPath)}
    if($Attempt){$arguments+=@('--attempt',$Attempt)}
    $raw=& $python @arguments
    if($LASTEXITCODE -ne 0){throw 'Row journal blocked; no automatic retry'}
    return (($raw -join "`n") | ConvertFrom-Json)
}
if($Operation -eq 'NextReviewed') {
    if(-not $ConfirmNavigation){throw 'Navigation confirmation required'}
    $gate=& $python -X utf8 (Join-Path $PSScriptRoot 'ginger-review-queue.py') gate-next --row-directory $QueueDirectory
    if($LASTEXITCODE -ne 0){throw 'Current review not queued; no input permitted'}
    $Operation='Step'
}
if($Operation -eq 'Status'){Invoke-Journal 'status' | ConvertTo-Json; return}
if($Operation -in @('Step','Recover') -and -not $ConfirmNavigation){throw 'Navigation confirmation required'}
if($Operation -ne 'Initialize') {
    $state=Invoke-Journal 'status'
    if($Operation -in @('Step','Recover') -and $null -ne $state.pending_attempt){throw 'Unresolved row navigation; no input permitted'}
    if($Operation -eq 'Step' -and $state.requires_read -and -not $state.selected_sampled){throw 'Selected row must be sampled before navigation'}
    if($Operation -in @('InspectSearch','InspectQuery','PrepareFilters','FinishReview') -and $null -ne $state.pending_attempt){throw 'Unresolved row navigation; observation blocked'}
    if($Operation -eq 'Read' -and ($null -ne $state.pending_attempt -or $state.selected_sampled)){throw 'Pending navigation or already sampled row'}
    if($Operation -eq 'Step' -and $null -eq $state.next_position){$state | ConvertTo-Json; return}
    if($Operation -eq 'Observe' -and $null -eq $state.pending_attempt){throw 'No pending row observation'}
}
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ginger-navigation.ps1'))
Invoke-Expression $source.Substring(0,$source.IndexOf('$processes = @('))
$targets=@(Get-Process Ginger4 -ErrorAction Stop | Where-Object MainWindowHandle -ne 0)
if($targets.Count -ne 1 -or [IO.Path]::GetFileName($targets[0].Path) -ine 'Ginger4.exe'){throw 'Exactly one visible Ginger executable required'}
$window=$targets[0].MainWindowHandle
$ownerId=[uint32]$targets[0].Id
$diagnostics=Join-Path $env:LOCALAPPDATA ('BabyAI/diagnostics/ginger-row-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($diagnostics)
trap {
    if((Get-Variable diagnostics -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $diagnostics)) {
        ($_ | Out-String) | Set-Content -LiteralPath (Join-Path $diagnostics 'failure.txt') -Encoding UTF8
    }
    break
}
function Save-Rows($Rows,[string]$Name) {
    $path=Join-Path $diagnostics $Name
    [pscustomobject]@{rows=@($Rows);process_id=$ownerId;can_write=$false} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
}
if($Operation -eq 'InspectSelection') {
    $evidence=@([GingerMenuAccess]::InspectProductSelection($window,$ownerId))
    $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $diagnostics 'selection-evidence.json') -Encoding UTF8
    $selected=@($evidence | Where-Object {($_.RowState -band 2) -ne 0 -or @($_.CellStates | Where-Object {($_ -band 2) -ne 0}).Count -gt 0} | ForEach-Object Position)
    [pscustomobject]@{observed_rows=$evidence.Count;selected_positions=$selected;diagnostics=$diagnostics;can_write=$false;input_sent=$false} | ConvertTo-Json -Depth 4
    return
}
if($Operation -eq 'Recover') {$rows=[GingerMenuAccess]::ReadProductRowsForRecovery($window,$ownerId)}
else {$rows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)}
$before=Save-Rows $rows 'before.json'
if($Operation -eq 'Initialize') {$result=Invoke-Journal 'create' $before}
elseif($Operation -eq 'FinishReview') {
    if(-not $PreparationDirectory){throw 'Preparation directory required'}
    $first=[GingerMenuAccess]::ReadSearchContext($window,$ownerId)
    Start-Sleep -Milliseconds 500
    $second=[GingerMenuAccess]::ReadSearchContext($window,$ownerId)
    $afterRows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)
    [GingerMenuAccess]::RequireSameProductRows($rows,$afterRows,$true)
    $reportPath=Join-Path $diagnostics 'finish-review.json'
    [pscustomobject]@{first=$first;second=$second;rows_report=[pscustomobject]@{rows=@($afterRows);process_id=$ownerId;can_write=$false}} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    $raw=& $python -X utf8 (Join-Path $PSScriptRoot 'ginger-row-review.py') --row-directory $QueueDirectory --preparation-directory $PreparationDirectory --report $reportPath
    if($LASTEXITCODE -ne 0){throw 'Row review blocked; no navigation permitted by this operation'}
    $result=($raw -join "`n") | ConvertFrom-Json
    $queued=& $python -X utf8 (Join-Path $PSScriptRoot 'ginger-review-queue.py') add --row-directory $QueueDirectory --report $result.report
    if($LASTEXITCODE -ne 0){throw 'Row read but review queue blocked; do not advance'}
    $result | Add-Member -NotePropertyName review_queue -NotePropertyValue (($queued -join "`n") | ConvertFrom-Json)
}
elseif($Operation -eq 'PrepareFilters') {
    $context=[GingerMenuAccess]::ReadSearchContext($window,$ownerId)
    $afterRows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)
    [GingerMenuAccess]::RequireSameProductRows($rows,$afterRows,$true)
    $reportPath=Join-Path $diagnostics 'prepare-filters.json'
    [pscustomobject]@{context=$context;rows_report=[pscustomobject]@{rows=@($afterRows);process_id=$ownerId;can_write=$false}} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    $planDirectory=Join-Path $diagnostics 'filter-plan'
    $proposal=Join-Path $diagnostics 'proposal.json'
    $raw=& $python -X utf8 (Join-Path $PSScriptRoot 'ginger-row-filter-prepare.py') --row-directory $QueueDirectory --report $reportPath --plan-directory $planDirectory --proposal $proposal
    if($LASTEXITCODE -ne 0){throw 'Filter preparation blocked; no input sent'}
    $result=[pscustomobject]@{proposal=(($raw -join "`n") | ConvertFrom-Json);plan_directory=if(Test-Path -LiteralPath $planDirectory){$planDirectory}else{$null};input_sent=$false}
}
elseif($Operation -eq 'InspectQuery') {
    $query=[GingerMenuAccess]::ReadSearchWords($window,$ownerId)
    $afterRows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)
    [GingerMenuAccess]::RequireSameProductRows($rows,$afterRows,$true)
    $selected=@($afterRows | Where-Object Selected)
    if($selected.Count -ne 1){throw 'Exactly one selected source required'}
    [GingerMenuAccess]::RequireSameSelectedProduct($selected[0].Observation,$query.Source)
    $query | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $diagnostics 'search-words.json') -Encoding UTF8
    $result=[pscustomobject]@{observed_position=$query.Source.ObservedRowPosition;search_word_count=@($query.Candidates).Count;checked_words=@($query.Candidates | Where-Object {$_.Checked -eq $true}).Count;unknown_check_state=@($query.Candidates | Where-Object {$null -eq $_.Checked}).Count;candidate_freshness_verified=$false;can_write=$false}
}
elseif($Operation -eq 'InspectSearch') {
    $context=[GingerMenuAccess]::ReadSearchContext($window,$ownerId)
    $afterRows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)
    [GingerMenuAccess]::RequireSameProductRows($rows,$afterRows,$true)
    $selected=@($afterRows | Where-Object Selected)
    if($selected.Count -ne 1){throw 'Exactly one selected product required'}
    [GingerMenuAccess]::RequireSameSelectedProduct($selected[0].Observation,$context.Products.Source)
    $path=Join-Path $diagnostics 'search-context.json'
    $context | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
    $result=[pscustomobject]@{observed_position=$context.Products.Source.ObservedRowPosition;query_words_match_source=$context.QueryWordsMatchSource;search_word_count=@($context.SearchWords.Candidates).Count;candidate_count=@($context.Products.Candidates).Count;candidate_freshness_verified=$false;can_write=$false}
}
elseif($Operation -eq 'Read') {
    $reading=[GingerMenuAccess]::ReadMatchingCandidates($window,$ownerId)
    $afterRows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)
    [GingerMenuAccess]::RequireSameProductRows($rows,$afterRows,$true)
    $path=Join-Path $diagnostics 'sample.json'
    [pscustomobject]@{rows=@($afterRows);process_id=$ownerId;can_write=$false;candidate_snapshot=$reading} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
    $result=Invoke-Journal 'sample' $path
}
elseif($Operation -eq 'Recover') {
    $intent=Invoke-Journal 'recover' $before
    [void][GingerMenuAccess]::RestoreProductSelection($window,$ownerId,$rows,[int]$intent.position,$true)
    $after=Save-Rows ([GingerMenuAccess]::ReadProductRows($window,$ownerId)) 'after.json'
    $result=Invoke-Journal 'restored' $after ([string]$intent.attempt)
}
elseif($Operation -eq 'Observe') {
    $action=if($state.pending_kind -eq 'recover'){'restored'}else{'observe'}
    $result=Invoke-Journal $action $before ([string]$state.pending_attempt)
}
else {
    # Persist before input. A crash, timeout or uncertain result leaves this unresolved.
    $intent=Invoke-Journal 'begin' $before
    [void][GingerMenuAccess]::SelectNextProduct($window,$ownerId,$rows,$true)
    $after=Save-Rows ([GingerMenuAccess]::ReadProductRows($window,$ownerId)) 'after.json'
    $result=Invoke-Journal 'observe' $after ([string]$intent.attempt)
}
[pscustomobject]@{status=$result;diagnostics=$diagnostics;can_write=$false;products_processed=$false} | ConvertTo-Json -Depth 5
