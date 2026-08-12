param(
    [ValidateSet("echo", "ollama")]
    [string]$Provider = "echo"
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "../..")
Set-Location $repo
$env:BABYAI_PROVIDER = $Provider

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
Write-Host "[BabyAI] $($desktopExe.FullName)"
Start-Process -FilePath $desktopExe.FullName -WorkingDirectory $desktopExe.DirectoryName
