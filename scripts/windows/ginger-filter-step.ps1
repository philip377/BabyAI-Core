# One bounded plan step. Pending actions can only be observed, never replayed.
[CmdletBinding()]
param([ValidateSet('Initialize','Step','Observe','Status')][string]$Operation='Status',
      [Parameter(Mandatory=$true)][string]$PlanDirectory,
      [string]$StepsFile,[switch]$ConfirmNavigation)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
if($Operation -eq 'Step' -and -not $ConfirmNavigation){throw 'Explicit filter confirmation required'}
$PlanDirectory=[IO.Path]::GetFullPath($PlanDirectory)
$lockPath=$PlanDirectory+'.controller.lock'
$lock=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
function Invoke-Plan([string]$Action,[string]$Report='') {
    $arguments=@('-X','utf8',(Join-Path $PSScriptRoot 'ginger-filter-plan.py'),$Action,'--directory',$PlanDirectory)
    if($Report){$arguments+=@('--report',$Report)}
    if($Action -eq 'begin'){$arguments+='--confirmed'}
    $raw=& python @arguments
    if($LASTEXITCODE -ne 0){throw 'Filter journal blocked; no automatic retry'}
    return (($raw -join "`n") | ConvertFrom-Json)
}
if($Operation -eq 'Status'){Invoke-Plan 'status' | ConvertTo-Json -Depth 5; return}
if($Operation -ne 'Initialize') {
    $state=Invoke-Plan 'status'
    if($Operation -eq 'Step' -and ($null -ne $state.pending_step -or $null -eq $state.next_step)){throw 'Pending or completed plan; no input allowed'}
    if($Operation -eq 'Observe' -and $null -eq $state.pending_step){throw 'No pending filter step'}
} else {
    if(Test-Path -LiteralPath $PlanDirectory){throw 'Existing plan cannot be reset'}
    if(-not $StepsFile){throw 'Explicit steps file required'}
    $rawSteps=Get-Content -LiteralPath $StepsFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $steps=@()
    foreach($item in $rawSteps){
        if($item.enabled -isnot [bool] -or $item.word -isnot [string]){throw 'Invalid filter steps'}
        $steps += [pscustomobject]@{word=$item.word;enabled=$item.enabled}
    }
}
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ginger-navigation.ps1'))
& ([scriptblock]::Create($source.Substring(0,$source.IndexOf('$processes = @('))))
$p=@(Get-Process Ginger4 | Where-Object MainWindowHandle -ne 0)
if($p.Count -ne 1 -or [IO.Path]::GetFileName($p[0].Path) -ine 'Ginger4.exe'){throw 'One Ginger process required'}
$window=$p[0].MainWindowHandle
$ownerId=[uint32]$p[0].Id
$diagnostics=Join-Path $env:LOCALAPPDATA ('BabyAI/diagnostics/filter-step-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($diagnostics)
function Save-Report($Value,[string]$Name) {
    $path=Join-Path $diagnostics $Name
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
    return $path
}
if($Operation -eq 'Step'){[GingerMenuAccess]::ActivateForRead($window,$ownerId)}
$first=[GingerMenuAccess]::ReadSearchContext($window,$ownerId)
if($Operation -eq 'Initialize') {
    $report=Save-Report ([pscustomobject]@{context=$first;process_id=$ownerId;steps=$steps}) 'initialize.json'
    $state=Invoke-Plan 'create' $report
} else {
    if($Operation -eq 'Step') {
        $report=Save-Report ([pscustomobject]@{context=$first;process_id=$ownerId}) 'before.json'
        # The journal checks this exact context and durably records begin BEFORE input.
        $state=Invoke-Plan 'begin' $report
        $action=$state.pending_action
        if($null -eq $action -or $action.enabled -isnot [bool]){throw 'Missing bound action'}
        # Native driver rereads and compares against the same snapshot immediately before input.
        [void][GingerMenuAccess]::SetSearchWord($window,$ownerId,$first.SearchWords,$action.word,$action.enabled,$true)
        $first=[GingerMenuAccess]::ReadSearchContext($window,$ownerId)
    }
    Start-Sleep -Milliseconds 500
    $second=[GingerMenuAccess]::ReadSearchContext($window,$ownerId)
    $report=Save-Report ([pscustomobject]@{first=$first;second=$second;process_id=$ownerId}) 'after.json'
    $state=Invoke-Plan 'observe' $report
    $candidatePath=Save-Report $second.Products 'candidates.json'
    & python -X utf8 (Join-Path $PSScriptRoot 'ginger-candidate-review.py') $candidatePath --output (Join-Path $diagnostics 'review.json')
    if($LASTEXITCODE -ne 0){throw 'Step observed but candidate review unavailable'}
}
[pscustomobject]@{state=$state;diagnostics=$diagnostics;can_write=$false;search_completion_verified=$false} | ConvertTo-Json -Depth 6
} finally {
    $lock.Dispose()
}
