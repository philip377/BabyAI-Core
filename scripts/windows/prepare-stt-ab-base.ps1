param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir
)

$ErrorActionPreference = "Stop"

$modelName = "sherpa-onnx-whisper-base"
$modelUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-base.tar.bz2"
$expectedArchiveSize = 207557382L
$requiredFiles = @(
    "base-encoder.int8.onnx",
    "base-decoder.int8.onnx",
    "base-tokens.txt"
)

$publishRoot = [IO.Path]::GetFullPath($PublishDir)
$workRoot = Join-Path $env:TEMP ("babyai-stt-ab-base-" + [Guid]::NewGuid().ToString("N"))
$archive = Join-Path $workRoot "$modelName.tar.bz2"
$extractRoot = Join-Path $workRoot "extract"

New-Item -ItemType Directory -Force $workRoot | Out-Null
New-Item -ItemType Directory -Force $extractRoot | Out-Null

try {
    Write-Host "Downloading experimental STT A/B model: $modelName"
    Invoke-WebRequest -Uri $modelUrl -OutFile $archive

    $actualSize = (Get-Item $archive).Length
    if ($actualSize -ne $expectedArchiveSize) {
        throw "Whisper Base archive size mismatch: expected $expectedArchiveSize, got $actualSize"
    }

    # The legacy upstream asset does not expose a digest in GitHub release metadata.
    # Record the observed hash for diagnostics, but do not pretend it is an authenticated pin.
    $observedSha256 = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
    Write-Host "Observed Whisper Base archive SHA256: $observedSha256"

    & tar.exe -xjf $archive -C $extractRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Could not extract the experimental Whisper Base model."
    }

    $modelRoot = Join-Path $extractRoot $modelName
    foreach ($file in $requiredFiles) {
        if (-not (Test-Path (Join-Path $modelRoot $file) -PathType Leaf)) {
            throw "Extracted Whisper Base model is incomplete: missing $file"
        }
    }

    $destination = Join-Path $publishRoot "stt\$modelName"
    New-Item -ItemType Directory -Force $destination | Out-Null
    foreach ($file in $requiredFiles) {
        Copy-Item (Join-Path $modelRoot $file) (Join-Path $destination $file) -Force
    }

    Write-Host "Bundled experimental STT A/B model: $destination"
}
finally {
    if (Test-Path $workRoot) {
        Remove-Item $workRoot -Recurse -Force
    }
}
