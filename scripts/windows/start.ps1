param(
    [ValidateSet("echo", "ollama", "native")]
    [string]$Provider = "echo",
    [string]$NativeModel = "",
    [string]$NativeRuntime = ""
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "../..")
$bootstrap = Join-Path $PSScriptRoot "bootstrap.ps1"
$run = Join-Path $PSScriptRoot "run.ps1"
Set-Location $repo

if ($NativeModel) {
    $env:BABYAI_NATIVE_MODEL = $NativeModel
}
if ($NativeRuntime) {
    $env:BABYAI_NATIVE_RUNTIME = $NativeRuntime
}

$needsBootstrap = $false
$pythonCommand = Get-Command python -ErrorAction SilentlyContinue
if (-not $pythonCommand) {
    $needsBootstrap = $true
} else {
    & $pythonCommand.Source -c "import babyai" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $needsBootstrap = $true
    }
}

$outputRoot = Join-Path $repo "desktop/BabyAI.Desktop/bin/x64/Release"
$desktopExe = Get-ChildItem -Path $outputRoot -Filter "BabyAI.Desktop.exe" -File -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $desktopExe) {
    $needsBootstrap = $true
}

if ($Provider -eq "ollama") {
    $ollamaCommand = Get-Command ollama -ErrorAction SilentlyContinue
    if (-not $ollamaCommand) {
        $needsBootstrap = $true
    } else {
        $models = ollama list 2>$null
        if ($LASTEXITCODE -ne 0 -or ($models -notmatch "qwen3:8b")) {
            $needsBootstrap = $true
        }
    }
}

if ($needsBootstrap) {
    Write-Host "[BabyAI] First-run setup or repair is required."
    & $bootstrap -Provider $Provider -NativeModel $NativeModel -NativeRuntime $NativeRuntime
}

Write-Host "[BabyAI] Launching..."
& $run -Provider $Provider -NativeModel $NativeModel -NativeRuntime $NativeRuntime
