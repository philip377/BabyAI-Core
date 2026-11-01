# Explicit development trial. One explicit desired-state action; never retries input.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Word,[Parameter(Mandatory=$true)][string]$ReceiptDirectory,[switch]$ConfirmNavigation,[switch]$Disable)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
if(-not $ConfirmNavigation){throw 'Filter confirmation required'}
if(Test-Path -LiteralPath $ReceiptDirectory){throw 'Existing filter receipt; inspect outcome instead of retrying'}
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ginger-navigation.ps1'))
Invoke-Expression $source.Substring(0,$source.IndexOf('$processes = @('))
$p=@(Get-Process Ginger4 | Where-Object MainWindowHandle -ne 0)
if($p.Count -ne 1 -or [IO.Path]::GetFileName($p[0].Path) -ine 'Ginger4.exe'){throw 'One Ginger process required'}
$window=$p[0].MainWindowHandle
$ownerId=[uint32]$p[0].Id
[GingerMenuAccess]::ActivateForRead($window,$ownerId)
$before=[GingerMenuAccess]::ReadSearchWords($window,$ownerId)
$counts=[GingerMenuAccess]::InspectMatching($window,$ownerId)
[void][IO.Directory]::CreateDirectory($ReceiptDirectory)
$intent=[pscustomobject]@{word=$Word;enabled=(-not $Disable);before=$before;counts=$counts;process_id=$ownerId;can_write=$false}
$stream=[IO.File]::Open((Join-Path $ReceiptDirectory 'intent.json'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try {$bytes=[Text.Encoding]::UTF8.GetBytes(($intent | ConvertTo-Json -Depth 8));$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)} finally {$stream.Dispose()}
try {
$after=[GingerMenuAccess]::SetSearchWord($window,$ownerId,$before,$Word,(-not $Disable),$true)
$countsAfter=[GingerMenuAccess]::InspectMatching($window,$ownerId)
[pscustomobject]@{word=$Word;enabled=(-not $Disable);after=$after;counts=$countsAfter;can_write=$false} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ReceiptDirectory 'observed.json') -Encoding UTF8
$observation=& "$env:WINDIR/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'ginger-search-observe.ps1') -ReceiptDirectory $ReceiptDirectory
if($LASTEXITCODE -ne 0){throw 'Filter observed but complete review unavailable; use read-only observation, never retry input'}
$review=($observation -join "`n") | ConvertFrom-Json
[pscustomobject]@{word=$Word;enabled=(-not $Disable);before_candidates=$counts.ProductRows;after_candidates=$countsAfter.ProductRows;search_completion_verified=$false;can_write=$false;receipt=$ReceiptDirectory;report=$review.report} | ConvertTo-Json

} catch {
    [pscustomobject]@{status='outcome_unverified';word=$Word;enabled=(-not $Disable);error=$_.Exception.Message;automatic_retry_allowed=$false;can_write=$false} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ReceiptDirectory 'failure.json') -Encoding UTF8
    throw
}
