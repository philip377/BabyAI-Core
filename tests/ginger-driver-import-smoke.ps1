# Compile the shared types only. Never activates or reads a real window.
$ErrorActionPreference = 'Stop'
$scripts = Join-Path $PSScriptRoot '../scripts/windows'
$source = [IO.File]::ReadAllText((Join-Path $scripts 'ginger-navigation.ps1'))
$boundary = $source.IndexOf('$processes = @(')
if ($boundary -lt 0) { throw 'Missing driver boundary' }
foreach ($confirmed in @($true, $false)) {
    $ConfirmNavigation = $confirmed
    $Mode = 'caller-mode'
    & ([scriptblock]::Create($source.Substring(0,$boundary)))
    if ([bool]$ConfirmNavigation -ne $confirmed -or $Mode -ne 'caller-mode') {
        throw 'Driver import overwrote caller parameters'
    }
}
if (-not [GingerMenuAccess]::IsActiveSelectionRgb(0,120,212)) { throw 'Shared types unavailable' }
foreach ($name in @('ginger-desktop-step','ginger-match-once','ginger-search-observe','ginger-filter-step','ginger-new-matching-tab','ginger-search-star','ginger-row-step','ginger-queue-step')) {
    $wrapper = [IO.File]::ReadAllText((Join-Path $scripts ($name + '.ps1')))
    if ($wrapper.Contains('Invoke-Expression $source.Substring')) { throw "Unsafe import in $name" }
    if (-not $wrapper.Contains('& ([scriptblock]::Create($source.Substring')) { throw "Missing isolated import in $name" }
}
Write-Output 'PASS: driver types loaded; granted and absent confirmations preserved; all wrappers isolated'
