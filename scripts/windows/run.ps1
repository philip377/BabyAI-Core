param(
    [ValidateSet("echo", "ollama")]
    [string]$Provider = "echo"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "../..")
Set-Location $repo
$env:BABYAI_PROVIDER = $Provider

if (-not (Get-Command babyai-desktop -ErrorAction SilentlyContinue)) {
    throw "BabyAI Core is not installed in this shell. Run .\scripts\windows\bootstrap.ps1 first."
}

if ($Provider -eq "ollama") {
    if (-not (Get-Command ollama -ErrorAction SilentlyContinue)) {
        throw "Ollama is not installed. Install it or run with -Provider echo."
    }
    $null = Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/tags" -TimeoutSec 3
}

babyai-desktop exec status | Out-Null
Write-Host "[BabyAI] Starting Windows Orb ($Provider provider)..."
dotnet run --project desktop/BabyAI.Desktop/BabyAI.Desktop.csproj -c Debug -p:Platform=x64
