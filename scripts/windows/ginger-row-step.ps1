# Explicit development controller: selection receipts only, no candidate freshness claim.
[CmdletBinding()]
param(
    [ValidateSet('Initialize','Step','Observe','Status')][string]$Operation='Status',
    [Parameter(Mandatory=$true)][string]$QueueDirectory,
    [switch]$ConfirmNavigation
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$python=(Get-Command python.exe -ErrorAction Stop).Source
function Invoke-Journal([string]$Action,[string]$ReportPath='',[string]$Attempt='') {
    $arguments=@('-X','utf8',(Join-Path $PSScriptRoot 'ginger-row-cli.py'),$Action,'--directory',$QueueDirectory)
    if($ReportPath){$arguments+=@('--report',$ReportPath)}
    if($Attempt){$arguments+=@('--attempt',$Attempt)}
    $raw=& $python @arguments
    if($LASTEXITCODE -ne 0){throw 'Row journal blocked; no automatic retry'}
    return (($raw -join "`n") | ConvertFrom-Json)
}
if($Operation -eq 'Status'){Invoke-Journal 'status' | ConvertTo-Json; return}
if($Operation -eq 'Step' -and -not $ConfirmNavigation){throw 'Navigation confirmation required'}
if($Operation -ne 'Initialize') {
    $state=Invoke-Journal 'status'
    if($Operation -eq 'Step' -and $null -ne $state.pending_attempt){throw 'Unresolved row navigation; no input permitted'}
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
$rows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)
$before=Save-Rows $rows 'before.json'
if($Operation -eq 'Initialize') {$result=Invoke-Journal 'create' $before}
elseif($Operation -eq 'Observe') {$result=Invoke-Journal 'observe' $before ([string]$state.pending_attempt)}
else {
    # Persist before input. A crash, timeout or uncertain result leaves this unresolved.
    $intent=Invoke-Journal 'begin' $before
    [void][GingerMenuAccess]::SelectNextProduct($window,$ownerId,$rows,$true)
    $after=Save-Rows ([GingerMenuAccess]::ReadProductRows($window,$ownerId)) 'after.json'
    $result=Invoke-Journal 'observe' $after ([string]$intent.attempt)
}
[pscustomobject]@{status=$result;diagnostics=$diagnostics;can_write=$false;products_processed=$false} | ConvertTo-Json -Depth 5
