param(
    [ValidateSet("echo", "ollama", "native")]
    [string]$Provider = "echo",
    [string]$NativeModel = "",
    [string]$NativeRuntime = ""
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "../..")
Set-Location $repo

function Require-Command([string]$Name, [string]$Hint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name was not found. $Hint"
    }
}

Require-Command "python" "Install Python 3.11+ and reopen PowerShell."
Require-Command "dotnet" "Install the .NET 10 SDK and reopen PowerShell."

$pythonVersion = python -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')"
$parts = $pythonVersion.Split('.')
if ([int]$parts[0] -lt 3 -or ([int]$parts[0] -eq 3 -and [int]$parts[1] -lt 11)) {
    throw "Python 3.11+ is required. Found $pythonVersion."
}

$dotnetVersion = dotnet --version
if ([int]($dotnetVersion.Split('.')[0]) -lt 10) {
    throw ".NET 10 SDK is required. Found $dotnetVersion."
}

Write-Host "[BabyAI] Installing Core..."
python -m pip install -e .

$env:BABYAI_PROVIDER = $Provider
if ($NativeModel) {
    $env:BABYAI_NATIVE_MODEL = $NativeModel
}
if ($NativeRuntime) {
    $env:BABYAI_NATIVE_RUNTIME = $NativeRuntime
}

if ($Provider -eq "ollama") {
    Require-Command "ollama" "Install Ollama, then run: ollama pull qwen3:8b"
    Write-Host "[BabyAI] Checking Ollama model..."
    $models = ollama list 2>$null
    if ($LASTEXITCODE -ne 0 -or ($models -notmatch "qwen3:8b")) {
        Write-Host "[BabyAI] qwen3:8b is missing. Pulling it now..."
        ollama pull qwen3:8b
    }
}

if ($Provider -eq "native") {
    $resolvedModel = python -c "from babyai.config import BabyAIConfig; print(BabyAIConfig.default().native_model_file)"
    $resolvedRuntime = python -c "from babyai.config import BabyAIConfig; print(BabyAIConfig.default().native_runtime_file)"
    if (-not (Test-Path -LiteralPath $resolvedModel -PathType Leaf)) {
        throw "Native GGUF model was not found at '$resolvedModel'. Pass -NativeModel <model.gguf> or set BABYAI_NATIVE_MODEL. BabyAI will not download a model automatically."
    }
    if (-not (Test-Path -LiteralPath $resolvedRuntime -PathType Leaf)) {
        throw "BabyAI native runtime was not found at '$resolvedRuntime'. Pass -NativeRuntime <babyai_native.dll> or set BABYAI_NATIVE_RUNTIME."
    }
    Write-Host "[BabyAI] Native model: $resolvedModel"
    Write-Host "[BabyAI] Native runtime: $resolvedRuntime"
}

Write-Host "[BabyAI] Initializing local state..."
python -m babyai.setup_cli init
python -m babyai.setup_cli doctor

Write-Host "[BabyAI] Verifying desktop command bridge..."
python -m babyai.desktop_commands_cli exec status | Out-Host

Write-Host "[BabyAI] Building runnable Windows Orb..."
dotnet build desktop/BabyAI.Desktop/BabyAI.Desktop.csproj -c Release -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "BabyAI Desktop build failed."
}

$outputRoot = Join-Path $repo "desktop/BabyAI.Desktop/bin/x64/Release"
$desktopExe = Get-ChildItem -Path $outputRoot -Filter "BabyAI.Desktop.exe" -File -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $desktopExe) {
    throw "BabyAI.Desktop.exe was not found after a successful build."
}

Write-Host ""
Write-Host "BabyAI Windows MVP is ready."
Write-Host "Desktop: $($desktopExe.FullName)"
if ($Provider -eq "native") {
    Write-Host "Smoke: powershell -ExecutionPolicy Bypass -File .\scripts\windows\native-smoke.ps1 -NativeModel `"$resolvedModel`" -NativeRuntime `"$resolvedRuntime`""
}
Write-Host "Run:  powershell -ExecutionPolicy Bypass -File .\scripts\windows\run.ps1 -Provider $Provider"
