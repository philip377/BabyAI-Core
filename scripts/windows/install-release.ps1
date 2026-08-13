param(
    [Parameter(Mandatory = $false)]
    [string]$BundleRoot = (Split-Path -Parent $PSScriptRoot),

    [Parameter(Mandatory = $false)]
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "BabyAI"),

    [Parameter(Mandatory = $false)]
    [string]$Python = "python",

    [Parameter(Mandatory = $false)]
    [string]$ModelPath = ""
)

$ErrorActionPreference = "Stop"

$manifestPath = Join-Path $BundleRoot "release.json"
if (-not (Test-Path $manifestPath -PathType Leaf)) {
    throw "Release manifest not found: $manifestPath"
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$version = [string]$manifest.version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Release manifest does not contain a version."
}

$desktopSource = Join-Path $BundleRoot "app"
$wheelSource = Join-Path $BundleRoot "wheels"
$runtimeSource = Join-Path $BundleRoot "runtime"
$launcherSource = Join-Path $BundleRoot "start.ps1"
foreach ($required in @($desktopSource, $wheelSource, $runtimeSource)) {
    if (-not (Test-Path $required -PathType Container)) {
        throw "Release bundle is incomplete: $required"
    }
}
if (-not (Test-Path $launcherSource -PathType Leaf)) {
    throw "Release launcher is missing: $launcherSource"
}

$versionsRoot = Join-Path $InstallRoot "versions"
$versionDir = Join-Path $versionsRoot $version
$tempDir = "$versionDir.installing"

New-Item -ItemType Directory -Force $versionsRoot | Out-Null
if (Test-Path $tempDir) {
    Remove-Item $tempDir -Recurse -Force
}
New-Item -ItemType Directory -Force $tempDir | Out-Null

try {
    Copy-Item $desktopSource (Join-Path $tempDir "app") -Recurse
    Copy-Item $runtimeSource (Join-Path $tempDir "runtime") -Recurse
    Copy-Item $wheelSource (Join-Path $tempDir "wheels") -Recurse

    $venv = Join-Path $tempDir "python"
    & $Python -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw "Could not create the BabyAI Python environment." }

    $venvPython = Join-Path $venv "Scripts\python.exe"
    $babyWheel = Get-ChildItem (Join-Path $tempDir "wheels") -Filter "babyai_core-*.whl" -File | Select-Object -First 1
    if (-not $babyWheel) { throw "BabyAI Core wheel was not found in the release bundle." }

    & $venvPython -m pip install --disable-pip-version-check --no-index --find-links (Join-Path $tempDir "wheels") $babyWheel.FullName
    if ($LASTEXITCODE -ne 0) { throw "Could not install BabyAI Core from the release bundle." }

    if (Test-Path $versionDir) {
        Remove-Item $versionDir -Recurse -Force
    }
    Move-Item $tempDir $versionDir

    $current = @{
        version = $version
        path = $versionDir
    } | ConvertTo-Json
    Set-Content (Join-Path $InstallRoot "current.json") $current -Encoding UTF8

    $preferencesPath = Join-Path $InstallRoot "launch.json"
    $preferences = @{
        provider = "native"
        acceleration = "cpu"
        model = $ModelPath
    }
    if (Test-Path $preferencesPath) {
        try {
            $existing = Get-Content $preferencesPath -Raw | ConvertFrom-Json
            if ($existing.provider) { $preferences.provider = [string]$existing.provider }
            if ($existing.acceleration) { $preferences.acceleration = [string]$existing.acceleration }
            if ([string]::IsNullOrWhiteSpace($ModelPath) -and $existing.model) { $preferences.model = [string]$existing.model }
        } catch {
            # Keep safe defaults when an old launch file cannot be read.
        }
    }
    Set-Content $preferencesPath ($preferences | ConvertTo-Json) -Encoding UTF8
    Copy-Item $launcherSource (Join-Path $InstallRoot "Start-BabyAI.ps1") -Force

    Write-Host "BabyAI $version installed to $versionDir"
    Write-Host "User data and GGUF models were not modified."
    Write-Host "Launcher: $(Join-Path $InstallRoot 'Start-BabyAI.ps1')"
}
finally {
    if (Test-Path $tempDir) {
        Remove-Item $tempDir -Recurse -Force
    }
}
