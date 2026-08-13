param(
    [Parameter(Mandatory = $true)]
    [string]$NativeModel,
    [Parameter(Mandatory = $true)]
    [string]$NativeRuntime,
    [string]$Prompt = "Reply with a short greeting from BabyAI native inference."
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "../..")
Set-Location $repo

if (-not (Test-Path -LiteralPath $NativeModel -PathType Leaf)) {
    throw "Native GGUF model was not found: $NativeModel"
}
if (-not (Test-Path -LiteralPath $NativeRuntime -PathType Leaf)) {
    throw "BabyAI native runtime was not found: $NativeRuntime"
}

$pythonCommand = Get-Command python -ErrorAction SilentlyContinue
if (-not $pythonCommand) {
    throw "Python was not found. Run .\scripts\windows\bootstrap.ps1 first."
}

$env:BABYAI_PROVIDER = "native"
$env:BABYAI_NATIVE_MODEL = (Resolve-Path -LiteralPath $NativeModel).Path
$env:BABYAI_NATIVE_RUNTIME = (Resolve-Path -LiteralPath $NativeRuntime).Path
$env:BABYAI_PYTHON = $pythonCommand.Source

& $env:BABYAI_PYTHON -c "import babyai" | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "BabyAI Core is not installed. Run .\scripts\windows\bootstrap.ps1 first."
}

Write-Host "[BabyAI native smoke] Model: $env:BABYAI_NATIVE_MODEL"
Write-Host "[BabyAI native smoke] Runtime: $env:BABYAI_NATIVE_RUNTIME"
Write-Host "[BabyAI native smoke] Checking read-only readiness..."
$statusRaw = & $env:BABYAI_PYTHON -m babyai.desktop_commands_cli exec status
if ($LASTEXITCODE -ne 0) {
    throw "Native readiness command failed."
}
$status = $statusRaw | ConvertFrom-Json
if (-not $status.ok -or -not $status.snapshot.runtime.ready -or $status.snapshot.runtime.provider -ne "native") {
    $detail = $status.snapshot.runtime.detail
    throw "Native provider is not ready: $detail"
}

Write-Host "[BabyAI native smoke] Running normal desktop chat path..."
$payload = @{ message = $Prompt } | ConvertTo-Json -Compress
$payloadArg = $payload.Replace('"', '\"')
$chatRaw = & $env:BABYAI_PYTHON -m babyai.desktop_commands_cli exec chat --payload $payloadArg
if ($LASTEXITCODE -ne 0) {
    throw "Native chat command failed."
}
$chat = $chatRaw | ConvertFrom-Json
if (-not $chat.ok -or -not $chat.reply -or -not $chat.reply.Trim()) {
    throw "Native chat returned no reply."
}

Write-Host ""
Write-Host "[BabyAI native smoke] PASS"
Write-Host "[BabyAI native smoke] Reply:"
Write-Host $chat.reply
