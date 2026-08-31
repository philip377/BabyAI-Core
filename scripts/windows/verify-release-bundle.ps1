param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "dist-release",

    [Parameter(Mandatory = $false)]
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$zipPath = Join-Path $OutputDir "BabyAI-$Version-windows-x64.zip"
if (-not (Test-Path $zipPath -PathType Leaf)) {
    throw "Release archive was not produced: $zipPath"
}

$workRoot = Join-Path $env:TEMP ("babyai-release-check-" + [Guid]::NewGuid().ToString("N"))
$expanded = Join-Path $workRoot "expanded"
$installRoot = Join-Path $workRoot "installed"
New-Item -ItemType Directory -Force $expanded | Out-Null

try {
    Expand-Archive $zipPath $expanded -Force
    $root = Join-Path $expanded "BabyAI-$Version-windows-x64"
    foreach ($required in @(
        "release.json",
        "model.json",
        "SHA256SUMS.txt",
        "install.ps1",
        "start.ps1",
        "app/BabyAI.Desktop.exe",
        "app/App.xbf",
        "app/MainWindow.xbf",
        "app/BabyAI.Desktop.pri",
        "app/stt/sherpa-onnx-whisper-tiny/tiny-encoder.int8.onnx",
        "app/stt/sherpa-onnx-whisper-tiny/tiny-decoder.int8.onnx",
        "app/stt/sherpa-onnx-whisper-tiny/tiny-tokens.txt",
        "runtime/cpu/babyai_native.dll",
        "runtime/cpu-avx/babyai_native.dll",
        "runtime/cpu-avx2/babyai_native.dll",
        "runtime/vulkan/babyai_native.dll"
    )) {
        if (-not (Test-Path (Join-Path $root $required) -PathType Leaf)) {
            throw "Release bundle is missing $required"
        }
    }

    foreach ($nativeName in @("sherpa-onnx-c-api.dll", "onnxruntime.dll")) {
        $native = Get-ChildItem (Join-Path $root "app") -Filter $nativeName -File -Recurse | Select-Object -First 1
        if (-not $native) {
            throw "Release bundle is missing the sherpa-onnx native runtime dependency: $nativeName"
        }
    }

    $manifest = Get-Content (Join-Path $root "release.json") -Raw | ConvertFrom-Json
    if ([string]$manifest.version -ne $Version) {
        throw "Release manifest version mismatch."
    }
    if ([string]$manifest.model_manifest -ne "model.json") {
        throw "Release manifest does not point to model.json."
    }
    foreach ($runtime in @("cpu", "cpu-avx", "cpu-avx2", "vulkan")) {
        if ($manifest.runtimes -notcontains $runtime) {
            throw "Release manifest is missing runtime tier $runtime"
        }
    }
    if ([bool]$manifest.python_included) {
        foreach ($required in @("python/python.exe", "python/babyai-runtime.json")) {
            if (-not (Test-Path (Join-Path $root $required) -PathType Leaf)) {
                throw "Self-contained release is missing $required"
            }
        }
    }

    $model = Get-Content (Join-Path $root "model.json") -Raw | ConvertFrom-Json
    if (-not ([string]$model.url).StartsWith("https://", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Production model URL must use HTTPS."
    }
    if ([string]$model.sha256 -ne "6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e") {
        throw "Production model SHA-256 changed unexpectedly."
    }
    if ([long]$model.size -ne 1117320736) {
        throw "Production model size changed unexpectedly."
    }
    if ([string]$model.filename -ne "babyai-qwen2.5-1.5b-instruct-q4_k_m.gguf") {
        throw "Production model filename changed unexpectedly."
    }

    & (Join-Path $root "install.ps1") -InstallRoot $installRoot -Python $Python
    if ($LASTEXITCODE -ne 0) {
        throw "Release installer failed."
    }

    $current = Get-Content (Join-Path $installRoot "current.json") -Raw | ConvertFrom-Json
    $installed = [string]$current.path
    if (-not (Test-Path (Join-Path $installed "app\BabyAI.Desktop.exe") -PathType Leaf)) {
        throw "Installed desktop executable is missing."
    }
    if (-not (Test-Path (Join-Path $installRoot "Start-BabyAI.ps1") -PathType Leaf)) {
        throw "Installed launcher is missing."
    }

    foreach ($required in @(
        "runtime\cpu\babyai_native.dll",
        "runtime\cpu-avx\babyai_native.dll",
        "runtime\cpu-avx2\babyai_native.dll",
        "runtime\vulkan\babyai_native.dll",
        "app\stt\sherpa-onnx-whisper-tiny\tiny-encoder.int8.onnx",
        "app\stt\sherpa-onnx-whisper-tiny\tiny-decoder.int8.onnx",
        "app\stt\sherpa-onnx-whisper-tiny\tiny-tokens.txt"
    )) {
        if (-not (Test-Path (Join-Path $installed $required) -PathType Leaf)) {
            throw "Installed release is missing $required"
        }
    }

    foreach ($nativeName in @("sherpa-onnx-c-api.dll", "onnxruntime.dll")) {
        $native = Get-ChildItem (Join-Path $installed "app") -Filter $nativeName -File -Recurse | Select-Object -First 1
        if (-not $native) {
            throw "Installed release is missing the sherpa-onnx native runtime dependency: $nativeName"
        }
    }

    $embeddedPython = Join-Path $installed "python\python.exe"
    $venvPython = Join-Path $installed "python\Scripts\python.exe"
    $installedPython = if (Test-Path $embeddedPython -PathType Leaf) { $embeddedPython } else { $venvPython }
    if (-not (Test-Path $installedPython -PathType Leaf)) {
        throw "Installed Python runtime is missing."
    }

    & $installedPython -c "import babyai; print('BabyAI release Core import OK')"
    if ($LASTEXITCODE -ne 0) {
        throw "Installed BabyAI Core could not be imported."
    }

    Write-Host "BabyAI release bundle and installer smoke passed."
    Write-Host "Self-contained Python: $([bool]$manifest.python_included)"
    Write-Host "Production model manifest: $([string]$model.display_name)"
    Write-Host "CPU runtime tiers verified: portable, AVX, AVX2"
    Write-Host "Local STT verified: sherpa-onnx + multilingual Whisper Tiny int8"
}
finally {
    if (Test-Path $workRoot) {
        Remove-Item $workRoot -Recurse -Force
    }
}
