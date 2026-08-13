param(
    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "release-wheels",

    [Parameter(Mandatory = $false)]
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force $OutputDir | Out-Null

& $Python -m pip wheel . --no-deps --wheel-dir $OutputDir
if ($LASTEXITCODE -ne 0) {
    throw "Could not build the BabyAI Core wheel."
}

foreach ($pythonVersion in @("311", "312", "313")) {
    & $Python -m pip download `
        --dest $OutputDir `
        --only-binary=:all: `
        --platform win_amd64 `
        --python-version $pythonVersion `
        --implementation cp `
        --abi "cp$pythonVersion" `
        "pydantic>=2.8" `
        "typer>=0.12"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not prepare dependencies for CPython $pythonVersion."
    }
}

if (-not (Get-ChildItem $OutputDir -Filter "babyai_core-*.whl" -File)) {
    throw "BabyAI Core wheel was not produced."
}

foreach ($pythonVersion in @("311", "312", "313")) {
    $nativeWheel = Get-ChildItem $OutputDir -Filter "pydantic_core-*-cp$pythonVersion-cp$pythonVersion-win_amd64.whl" -File | Select-Object -First 1
    if (-not $nativeWheel) {
        throw "pydantic-core wheel is missing for CPython $pythonVersion."
    }
}

Write-Host "Offline BabyAI wheels are ready: $OutputDir"
