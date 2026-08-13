param(
    [ValidateSet("echo", "ollama", "native")]
    [string]$Provider = "echo",
    [string]$NativeModel = "",
    [string]$NativeRuntime = ""
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "../..")
Set-Location $repo
$env:BABYAI_PROVIDER = $Provider
if ($NativeModel) {
    $env:BABYAI_NATIVE_MODEL = $NativeModel
}
if ($NativeRuntime) {
    $env:BABYAI_NATIVE_RUNTIME = $NativeRuntime
}

$pythonCommand = Get-Command python -ErrorAction SilentlyContinue
if (-not $pythonCommand) {
    throw "Python was not found. Install Python 3.11+ and run .\scripts\windows\bootstrap.ps1 first."
}
$env:BABYAI_PYTHON = $pythonCommand.Source

try {
    & $env:BABYAI_PYTHON -c "import babyai" | Out-Null
} catch {
    throw "BabyAI Core is not installed. Run .\scripts\windows\bootstrap.ps1 first."
}
if ($LASTEXITCODE -ne 0) {
    throw "BabyAI Core is not installed. Run .\scripts\windows\bootstrap.ps1 first."
}

if ($Provider -eq "ollama") {
    if (-not (Get-Command ollama -ErrorAction SilentlyContinue)) {
        throw "Ollama is not installed. Install it or run with -Provider echo."
    }
    $null = Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/tags" -TimeoutSec 3
}

if ($Provider -eq "native") {
    $resolvedModel = & $env:BABYAI_PYTHON -c "from babyai.config import BabyAIConfig; print(BabyAIConfig.default().native_model_file)"
    $resolvedRuntime = & $env:BABYAI_PYTHON -c "from babyai.config import BabyAIConfig; print(BabyAIConfig.default().native_runtime_file)"
    if (-not (Test-Path -LiteralPath $resolvedModel -PathType Leaf)) {
        throw "Native GGUF model was not found at '$resolvedModel'. Pass -NativeModel <model.gguf> or set BABYAI_NATIVE_MODEL."
    }
    if (-not (Test-Path -LiteralPath $resolvedRuntime -PathType Leaf)) {
        throw "BabyAI native runtime was not found at '$resolvedRuntime'. Pass -NativeRuntime <babyai_native.dll> or set BABYAI_NATIVE_RUNTIME."
    }
}

& $env:BABYAI_PYTHON -m babyai.desktop_commands_cli exec status | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "BabyAI desktop bridge check failed. Run .\scripts\windows\bootstrap.ps1 again."
}

$outputRoot = Join-Path $repo "desktop/BabyAI.Desktop/bin/x64/Release"
$desktopExe = Get-ChildItem -Path $outputRoot -Filter "BabyAI.Desktop.exe" -File -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $desktopExe) {
    Write-Host "[BabyAI] Release build is missing; building it now..."
    dotnet build desktop/BabyAI.Desktop/BabyAI.Desktop.csproj -c Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw "BabyAI Desktop build failed."
    }
    $desktopExe = Get-ChildItem -Path $outputRoot -Filter "BabyAI.Desktop.exe" -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

if (-not $desktopExe) {
    throw "BabyAI.Desktop.exe was not found. Run .\scripts\windows\bootstrap.ps1 again."
}

Write-Host "[BabyAI] Starting Windows Orb ($Provider provider)..."
if ($Provider -eq "native") {
    Write-Host "[BabyAI] Native model: $resolvedModel"
    Write-Host "[BabyAI] Native runtime: $resolvedRuntime"
}
Write-Host "[BabyAI] $($desktopExe.FullName)"
Start-Process -FilePath $desktopExe.FullName -WorkingDirectory $desktopExe.DirectoryName
