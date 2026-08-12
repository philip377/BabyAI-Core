param(
    [ValidateSet("echo", "ollama")]
    [string]$Provider = "echo",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Continue"
$repo = Resolve-Path (Join-Path $PSScriptRoot "../..")
Set-Location $repo
$env:BABYAI_PROVIDER = $Provider

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repo "babyai-diagnostics.txt"
} elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $repo $OutputPath
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

$lines = [System.Collections.Generic.List[string]]::new()
function Add-Line([string]$Text) {
    [void]$script:lines.Add($Text)
}

Add-Line "BabyAI Windows Diagnostics v1"
Add-Line "timestamp_utc=$([DateTime]::UtcNow.ToString('o'))"
Add-Line "provider=$Provider"
Add-Line "model=$(if ($env:BABYAI_MODEL) { $env:BABYAI_MODEL } else { 'qwen3:8b' })"
Add-Line "os=$([System.Runtime.InteropServices.RuntimeInformation]::OSDescription)"
Add-Line "architecture=$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
Add-Line "powershell=$($PSVersionTable.PSVersion)"

$pythonCommand = Get-Command python -ErrorAction SilentlyContinue
if ($pythonCommand) {
    $pythonVersion = & $pythonCommand.Source --version 2>&1 | Out-String
    Add-Line "python.present=true"
    Add-Line "python.version=$($pythonVersion.Trim())"

    & $pythonCommand.Source -c "import babyai" 2>$null | Out-Null
    Add-Line "core.import=$(if ($LASTEXITCODE -eq 0) { 'ok' } else { 'fail' })"

    & $pythonCommand.Source -m babyai.setup_cli doctor --skip-brain *> $null
    Add-Line "core.doctor_exit=$LASTEXITCODE"

    $bridgeRaw = & $pythonCommand.Source -m babyai.desktop_commands_cli exec status 2>$null
    $bridgeExit = $LASTEXITCODE
    Add-Line "bridge.exit=$bridgeExit"
    if ($bridgeExit -eq 0 -and $bridgeRaw) {
        try {
            $bridge = ($bridgeRaw | Out-String) | ConvertFrom-Json
            Add-Line "bridge.ok=$($bridge.ok.ToString().ToLowerInvariant())"
            Add-Line "bridge.schema_version=$($bridge.snapshot.schema_version)"
        } catch {
            Add-Line "bridge.parse=fail"
        }
    }
} else {
    Add-Line "python.present=false"
    Add-Line "core.import=not_checked"
    Add-Line "core.doctor_exit=not_checked"
    Add-Line "bridge.exit=not_checked"
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnetCommand) {
    $dotnetVersion = dotnet --version 2>&1 | Out-String
    Add-Line "dotnet.present=true"
    Add-Line "dotnet.version=$($dotnetVersion.Trim())"
} else {
    Add-Line "dotnet.present=false"
}

if ($Provider -eq "ollama") {
    $ollamaCommand = Get-Command ollama -ErrorAction SilentlyContinue
    if ($ollamaCommand) {
        Add-Line "ollama.present=true"
        $ollamaVersion = ollama --version 2>&1 | Out-String
        Add-Line "ollama.version=$($ollamaVersion.Trim())"
        $models = ollama list 2>$null
        Add-Line "ollama.qwen3_8b=$(if ($LASTEXITCODE -eq 0 -and ($models -match 'qwen3:8b')) { 'present' } else { 'missing' })"
        try {
            $null = Invoke-RestMethod -Uri "http://127.0.0.1:11434/api/tags" -TimeoutSec 3
            Add-Line "ollama.api=reachable"
        } catch {
            Add-Line "ollama.api=unreachable"
        }
    } else {
        Add-Line "ollama.present=false"
        Add-Line "ollama.qwen3_8b=not_checked"
        Add-Line "ollama.api=not_checked"
    }
} else {
    Add-Line "ollama.check=skipped_echo_provider"
}

$outputRoot = Join-Path $repo "desktop/BabyAI.Desktop/bin/x64/Release"
$desktopExe = Get-ChildItem -Path $outputRoot -Filter "BabyAI.Desktop.exe" -File -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($desktopExe) {
    Add-Line "desktop.exe=present"
    Add-Line "desktop.relative_path=$([System.IO.Path]::GetRelativePath($repo, $desktopExe.FullName))"
} else {
    Add-Line "desktop.exe=missing"
}

$desktopProcess = Get-Process -Name "BabyAI.Desktop" -ErrorAction SilentlyContinue
Add-Line "desktop.running=$(if ($desktopProcess) { 'true' } else { 'false' })"

Add-Line "privacy_note=no memory, chat, task, identity, permission contents, or user-file contents are included"
$lines | Set-Content -Path $OutputPath -Encoding UTF8

Write-Host "[BabyAI] Diagnostics written to: $OutputPath"
