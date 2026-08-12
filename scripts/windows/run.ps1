param(
    [ValidateSet("echo", "ollama")]
    [string]$Provider = "echo"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "../..")
Set-Location $repo
$env:BABYAI_PROVIDER = $Provider

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python was not found. Install Python 3.11+ and run .\scripts\windows\bootstrap.ps1 first."
}

try {
    python -c "import babyai" | Out-Null
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

python -m babyai.desktop_commands_cli exec status | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "BabyAI desktop bridge check failed. Run .\scripts\windows\bootstrap.ps1 again."
}

Write-Host "[BabyAI] Starting Windows Orb ($Provider provider)..."
dotnet run --project desktop/BabyAI.Desktop/BabyAI.Desktop.csproj -c Debug -p:Platform=x64
