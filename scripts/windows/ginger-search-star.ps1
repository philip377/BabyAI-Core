# Explicit development trial. One enable action; never toggles off or retries input.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Word,[Parameter(Mandatory=$true)][string]$ReceiptDirectory,[switch]$ConfirmNavigation)
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
$intent=[pscustomobject]@{word=$Word;before=$before;counts=$counts;process_id=$ownerId;can_write=$false}
$stream=[IO.File]::Open((Join-Path $ReceiptDirectory 'intent.json'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try {$bytes=[Text.Encoding]::UTF8.GetBytes(($intent | ConvertTo-Json -Depth 8));$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)} finally {$stream.Dispose()}
$after=[GingerMenuAccess]::EnableSearchWord($window,$ownerId,$before,$Word,$true)
$countsAfter=[GingerMenuAccess]::InspectMatching($window,$ownerId)
[pscustomobject]@{word=$Word;after=$after;counts=$countsAfter;can_write=$false} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ReceiptDirectory 'observed.json') -Encoding UTF8
[pscustomobject]@{word=$Word;enabled=$true;before_candidates=$counts.ProductRows;after_candidates=$countsAfter.ProductRows;search_completion_verified=$false;can_write=$false;receipt=$ReceiptDirectory} | ConvertTo-Json
