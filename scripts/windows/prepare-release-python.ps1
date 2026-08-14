param(
    [Parameter(Mandatory = $true)]
    [string]$WheelsDir,

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "release-python",

    [Parameter(Mandatory = $false)]
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
$WheelsDir = (Resolve-Path $WheelsDir).Path
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

$sourcePrefix = (& $Python -c "import sys; print(sys.prefix)" | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourcePrefix)) {
    throw "Could not determine the release Python prefix."
}
$sourcePrefix = (Resolve-Path $sourcePrefix).Path

$pythonVersion = (& $Python -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')" | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($pythonVersion)) {
    throw "Could not determine the release Python version."
}

if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Force $OutputDir | Out-Null

# The CPython Windows layout discovers Lib and DLLs relative to python.exe.
# Copying the setup-python prefix gives BabyAI a relocatable runtime without
# requiring a system-wide Python installation on the destination machine.
Get-ChildItem $sourcePrefix -Force | ForEach-Object {
    Copy-Item $_.FullName $OutputDir -Recurse -Force
}

$runtimePython = Join-Path $OutputDir "python.exe"
if (-not (Test-Path $runtimePython -PathType Leaf)) {
    throw "Copied Python runtime does not contain python.exe."
}

$sitePackages = Join-Path $OutputDir "Lib\site-packages"
New-Item -ItemType Directory -Force $sitePackages | Out-Null
$babyWheel = Get-ChildItem $WheelsDir -Filter "babyai_core-*.whl" -File | Select-Object -First 1
if (-not $babyWheel) {
    throw "BabyAI Core wheel was not found in $WheelsDir."
}

# Populate the copied runtime from the already-prepared offline wheel set.
# Runtime installation therefore performs no network access and needs no pip.
& $Python -m pip install `
    --disable-pip-version-check `
    --no-index `
    --find-links $WheelsDir `
    --target $sitePackages `
    --upgrade `
    $babyWheel.FullName
if ($LASTEXITCODE -ne 0) {
    throw "Could not populate the self-contained Python runtime."
}

& $runtimePython -c "import babyai, sys; print(f'BabyAI embedded Python {sys.version_info.major}.{sys.version_info.minor} import OK')"
if ($LASTEXITCODE -ne 0) {
    throw "The copied Python runtime cannot import BabyAI Core."
}

$metadata = [ordered]@{
    schema = 1
    python_version = $pythonVersion
    architecture = "x64"
    layout = "relocatable-prefix"
} | ConvertTo-Json
Set-Content (Join-Path $OutputDir "babyai-runtime.json") $metadata -Encoding UTF8

Write-Host "Self-contained Python runtime ready: $OutputDir"
