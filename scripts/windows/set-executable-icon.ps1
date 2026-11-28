param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$Icon
)

$ErrorActionPreference = 'Stop'
if (-not ('BabyAIExecutableIcon' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'ExecutableIcon.cs')
}
[BabyAIExecutableIcon]::Apply((Resolve-Path -LiteralPath $Executable).Path, (Resolve-Path -LiteralPath $Icon).Path)
