param(
    [Parameter(Mandatory = $false)]
    [string]$BundleRoot = $PSScriptRoot,

    [Parameter(Mandatory = $false)]
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "BabyAI"),

    [Parameter(Mandatory = $false)]
    [string]$Python = "python",

    [Parameter(Mandatory = $false)]
    [string]$ModelPath = ""
)

$ErrorActionPreference = "Stop"
$BundleRoot = (Resolve-Path $BundleRoot).Path

$checksumsPath = Join-Path $BundleRoot "SHA256SUMS.txt"
if (-not (Test-Path $checksumsPath -PathType Leaf)) {
    throw "Release checksums not found: $checksumsPath"
}

$bundlePrefix = $BundleRoot.TrimEnd('\') + '\'
foreach ($line in Get-Content $checksumsPath) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $match = [regex]::Match($line, '^([0-9a-fA-F]{64})  (.+)$')
    if (-not $match.Success) { throw "Invalid release checksum entry." }

    $expected = $match.Groups[1].Value.ToLowerInvariant()
    $relative = $match.Groups[2].Value.Replace('/', '\')
    $filePath = [IO.Path]::GetFullPath((Join-Path $BundleRoot $relative))
    if (-not $filePath.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release checksum path escapes the bundle."
    }
    if (-not (Test-Path $filePath -PathType Leaf)) {
        throw "Release file is missing: $relative"
    }
    $actual = (Get-FileHash $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "Release checksum mismatch: $relative"
    }
}

$manifestPath = Join-Path $BundleRoot "release.json"
if (-not (Test-Path $manifestPath -PathType Leaf)) {
    throw "Release manifest not found: $manifestPath"
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$version = [string]$manifest.version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Release manifest does not contain a version."
}

$bundledPython = [bool]$manifest.python_included
$pythonSource = Join-Path $BundleRoot "python"
if ($bundledPython) {
    $bundledPythonExe = Join-Path $pythonSource "python.exe"
    if (-not (Test-Path $bundledPythonExe -PathType Leaf)) {
        throw "Release manifest declares bundled Python, but python.exe is missing."
    }
} else {
    $supportedPython = @($manifest.python_versions | ForEach-Object { [string]$_ })
    $pythonVersion = (& $Python -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')" | Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($pythonVersion)) {
        throw "Could not determine the Python version."
    }
    if ($pythonVersion -notin $supportedPython) {
        throw "BabyAI $version supports Python $($supportedPython -join ', '); found Python $pythonVersion."
    }
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

    if ($bundledPython) {
        Copy-Item $pythonSource (Join-Path $tempDir "python") -Recurse
        $installedPython = Join-Path $tempDir "python\python.exe"
        & $installedPython -c "import babyai; print('Bundled BabyAI Core import OK')"
        if ($LASTEXITCODE -ne 0) {
            throw "Bundled BabyAI Core could not be imported."
        }
    } else {
        $venv = Join-Path $tempDir "python"
        & $Python -m venv $venv
        if ($LASTEXITCODE -ne 0) { throw "Could not create the BabyAI Python environment." }

        $venvPython = Join-Path $venv "Scripts\python.exe"
        $babyWheel = Get-ChildItem (Join-Path $tempDir "wheels") -Filter "babyai_core-*.whl" -File | Select-Object -First 1
        if (-not $babyWheel) { throw "BabyAI Core wheel was not found in the release bundle." }

        & $venvPython -m pip install --disable-pip-version-check --no-index --find-links (Join-Path $tempDir "wheels") $babyWheel.FullName
        if ($LASTEXITCODE -ne 0) { throw "Could not install BabyAI Core from the release bundle." }
    }

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
        acceleration = "auto"
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
    Write-Host "Runtime acceleration defaults to auto (Vulkan when usable, portable CPU fallback otherwise)."
    Write-Host "Bundled Python runtime: $bundledPython"
    Write-Host "Launcher: $(Join-Path $InstallRoot 'Start-BabyAI.ps1')"
}
finally {
    if (Test-Path $tempDir) {
        Remove-Item $tempDir -Recurse -Force
    }
}
