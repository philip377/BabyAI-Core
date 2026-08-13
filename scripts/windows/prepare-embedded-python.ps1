param(
    [Parameter(Mandatory = $true)]
    [string]$PythonArchive,

    [Parameter(Mandatory = $true)]
    [string]$WheelsDir,

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "release-python",

    [Parameter(Mandatory = $false)]
    [string]$BuildPython = "python"
)

$ErrorActionPreference = "Stop"
$PythonArchive = (Resolve-Path $PythonArchive).Path
$WheelsDir = (Resolve-Path $WheelsDir).Path
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Force $OutputDir | Out-Null
Expand-Archive $PythonArchive $OutputDir -Force

$pthPath = Join-Path $OutputDir "python312._pth"
if (-not (Test-Path $pthPath -PathType Leaf)) {
    throw "Embedded Python path configuration was not found."
}
Set-Content $pthPath @(
    "python312.zip",
    ".",
    "Lib\site-packages",
    "import site"
) -Encoding ASCII

$sitePackages = Join-Path $OutputDir "Lib\site-packages"
New-Item -ItemType Directory -Force $sitePackages | Out-Null
$babyWheel = Get-ChildItem $WheelsDir -Filter "babyai_core-*.whl" -File | Select-Object -First 1
if (-not $babyWheel) {
    throw "BabyAI Core wheel was not found."
}

& $BuildPython -m pip install `
    --disable-pip-version-check `
    --no-index `
    --find-links $WheelsDir `
    --target $sitePackages `
    $babyWheel.FullName
if ($LASTEXITCODE -ne 0) {
    throw "Could not populate the embedded BabyAI Python runtime."
}

$embeddedPython = Join-Path $OutputDir "python.exe"
& $embeddedPython -c "import babyai, pydantic, typer; print('BabyAI embedded Python OK')"
if ($LASTEXITCODE -ne 0) {
    throw "Embedded BabyAI Python runtime smoke failed."
}

Write-Host "Embedded BabyAI Python prepared: $OutputDir"
