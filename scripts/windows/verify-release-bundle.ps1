param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "dist-release",

    [Parameter(Mandatory = $false)]
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$zipPath = Join-Path $OutputDir "BabyAI-$Version-windows-x64.zip"
if (-not (Test-Path $zipPath -PathType Leaf)) {
    throw "Release archive was not produced: $zipPath"
}

$workRoot = Join-Path $env:TEMP ("babyai-release-check-" + [Guid]::NewGuid().ToString("N"))
$expanded = Join-Path $workRoot "expanded"
$installRoot = Join-Path $workRoot "installed"
New-Item -ItemType Directory -Force $expanded | Out-Null

try {
    Expand-Archive $zipPath $expanded -Force
    $root = Join-Path $expanded "BabyAI-$Version-windows-x64"
    foreach ($required in @(
        "release.json",
        "SHA256SUMS.txt",
        "install.ps1",
        "start.ps1",
        "app/BabyAI.Desktop.exe",
        "runtime/cpu/babyai_native.dll",
        "runtime/vulkan/babyai_native.dll"
    )) {
        if (-not (Test-Path (Join-Path $root $required) -PathType Leaf)) {
            throw "Release bundle is missing $required"
        }
    }

    $manifest = Get-Content (Join-Path $root "release.json") -Raw | ConvertFrom-Json
    if ([string]$manifest.version -ne $Version) {
        throw "Release manifest version mismatch."
    }
    if ([bool]$manifest.python_included) {
        foreach ($required in @("python/python.exe", "python/babyai-runtime.json")) {
            if (-not (Test-Path (Join-Path $root $required) -PathType Leaf)) {
                throw "Self-contained release is missing $required"
            }
        }
    }

    & (Join-Path $root "install.ps1") -InstallRoot $installRoot -Python $Python
    if ($LASTEXITCODE -ne 0) {
        throw "Release installer failed."
    }

    $current = Get-Content (Join-Path $installRoot "current.json") -Raw | ConvertFrom-Json
    $installed = [string]$current.path
    if (-not (Test-Path (Join-Path $installed "app\BabyAI.Desktop.exe") -PathType Leaf)) {
        throw "Installed desktop executable is missing."
    }
    if (-not (Test-Path (Join-Path $installRoot "Start-BabyAI.ps1") -PathType Leaf)) {
        throw "Installed launcher is missing."
    }

    $embeddedPython = Join-Path $installed "python\python.exe"
    $venvPython = Join-Path $installed "python\Scripts\python.exe"
    $installedPython = if (Test-Path $embeddedPython -PathType Leaf) { $embeddedPython } else { $venvPython }
    if (-not (Test-Path $installedPython -PathType Leaf)) {
        throw "Installed Python runtime is missing."
    }

    & $installedPython -c "import babyai; print('BabyAI release Core import OK')"
    if ($LASTEXITCODE -ne 0) {
        throw "Installed BabyAI Core could not be imported."
    }

    Write-Host "BabyAI release bundle and installer smoke passed."
    Write-Host "Self-contained Python: $([bool]$manifest.python_included)"
}
finally {
    if (Test-Path $workRoot) {
        Remove-Item $workRoot -Recurse -Force
    }
}
