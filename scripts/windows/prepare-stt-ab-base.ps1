param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir
)

$ErrorActionPreference = "Stop"

$modelName = "sherpa-onnx-whisper-base"
$modelUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-base.tar.bz2"
$expectedArchiveSize = 207557382L
# GitHub's legacy release metadata exposes no digest for this asset. This SHA256
# was measured from the official HTTPS asset during the first diagnostic CI run.
$expectedArchiveSha256 = "911b2083efd7c0dca2ac3b358b75222660dc09fb716d64fbfc417ba6c99ff3de"
$requiredFiles = @(
    "base-encoder.int8.onnx",
    "base-decoder.int8.onnx",
    "base-tokens.txt"
)

# MSBuild's PublishDir commonly ends in a backslash. When passed through Exec's
# quoted command line that trailing slash can surface as a literal trailing quote.
# Normalize only that transport artifact; never guess or redirect the output root.
$normalizedPublishDir = $PublishDir.Trim().Trim('"')
$publishRoot = [IO.Path]::GetFullPath($normalizedPublishDir)
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

    $actualSha256 = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedArchiveSha256) {
        throw "Whisper Base archive SHA256 mismatch: $actualSha256"
    }
    Write-Host "Verified Whisper Base archive SHA256: $actualSha256"

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
