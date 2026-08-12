param(
    [ValidateSet("echo", "ollama")]
    [string]$Provider = "echo"
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
if ($Provider -eq "ollama") {
    Require-Command "ollama" "Install Ollama, then run: ollama pull qwen3:8b"
    Write-Host "[BabyAI] Checking Ollama model..."
    $models = ollama list 2>$null
    if ($LASTEXITCODE -ne 0 -or ($models -notmatch "qwen3:8b")) {
        Write-Host "[BabyAI] qwen3:8b is missing. Pulling it now..."
        ollama pull qwen3:8b
    }
}

Write-Host "[BabyAI] Initializing local state..."
babyai-setup init
babyai-setup doctor

Write-Host "[BabyAI] Verifying desktop command bridge..."
babyai-desktop exec status | Out-Host

Write-Host "[BabyAI] Building Windows Orb..."
dotnet build desktop/BabyAI.Desktop/BabyAI.Desktop.csproj -c Debug -p:Platform=x64

Write-Host ""
Write-Host "BabyAI Windows MVP is ready."
Write-Host "Run:  powershell -ExecutionPolicy Bypass -File .\scripts\windows\run.ps1 -Provider $Provider"
