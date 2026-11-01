# Read-only reconciliation: never activates a window or repeats filter input.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReceiptDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$intentPath=Join-Path $ReceiptDirectory 'intent.json'
$intent=Get-Content -LiteralPath $intentPath -Raw -Encoding UTF8 | ConvertFrom-Json
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ginger-navigation.ps1'))
Invoke-Expression $source.Substring(0,$source.IndexOf('$processes = @('))
$p=@(Get-Process Ginger4 | Where-Object MainWindowHandle -ne 0)
if($p.Count -ne 1 -or $p[0].Id -ne $intent.process_id -or [IO.Path]::GetFileName($p[0].Path) -ine 'Ginger4.exe'){throw 'Ginger process changed; no automatic recovery'}
$observationDirectory=Join-Path $ReceiptDirectory ('observation-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($observationDirectory)
foreach($name in @('first','second')) {
    $context=[GingerMenuAccess]::ReadSearchContext($p[0].MainWindowHandle,[uint32]$p[0].Id)
    $context | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $observationDirectory ($name+'.json')) -Encoding UTF8
    if($name -eq 'first'){Start-Sleep -Milliseconds 500}
}
$output=Join-Path $observationDirectory 'review.json'
& python -X utf8 (Join-Path $PSScriptRoot 'ginger-search-session.py') $intentPath (Join-Path $observationDirectory 'first.json') (Join-Path $observationDirectory 'second.json') --process-id $p[0].Id --output $output
if($LASTEXITCODE -ne 0){throw 'Observation reconciliation failed; no input repeated'}
[pscustomobject]@{report=$output;input_sent=$false;search_completion_verified=$false;can_write=$false} | ConvertTo-Json
