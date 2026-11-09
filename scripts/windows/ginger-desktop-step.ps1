# Bundled desktop controller. Every transition consumes an explicit, fresh observation.
[CmdletBinding()]
param(
 [ValidateSet('Observe','Select','Filter')][string]$Operation='Observe',
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
[GingerMenuAccess]::ActivateForRead($window,$ownerId)
$rows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)
if($Operation -ne 'Observe') {
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
$rows=[GingerMenuAccess]::ReadProductRows($window,$ownerId)
$first=[GingerMenuAccess]::ReadSearchContext($window,$ownerId)
Start-Sleep -Milliseconds 700
$second=[GingerMenuAccess]::ReadSearchContext($window,$ownerId)
[GingerMenuAccess]::RequireSameSearchContext($first,$second)
[GingerMenuAccess]::RequireSameProductRows($rows,[GingerMenuAccess]::ReadProductRows($window,$ownerId),$true)
if(-not $second.QueryWordsMatchSource){throw 'Search query differs from source'}
[pscustomobject]@{process_id=$ownerId;rows=$rows} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'before.json') -Encoding UTF8
$second | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'search-context.json') -Encoding UTF8
[pscustomobject]@{status='observed';directory=$OutputDirectory;source=$second.Products.Source;candidates=@($second.Products.Candidates);words=@($second.SearchWords.Candidates);row_count=$rows.Length;process_id=$ownerId;search_completion_verified=$false} | ConvertTo-Json -Depth 8
