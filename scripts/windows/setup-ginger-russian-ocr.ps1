# Install Russian Tesseract traineddata for Ginger CPU OCR, per-user only.
# Downloads the official Tesseract tessdata_fast Russian model from GitHub.
# Does not start Ginger, use administrator privileges, or modify installed files.
[CmdletBinding()]
param(
    [string]$TesseractExe = "",
    [string]$TargetDirectory = ""
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") { throw "Windows only" }

if (-not $TargetDirectory) {
    $TargetDirectory = Join-Path $env:LOCALAPPDATA "BabyAI\Tesseract\tessdata"
}
if ([string]::IsNullOrWhiteSpace($TesseractExe)) {
    $found = Get-Command tesseract.exe -ErrorAction SilentlyContinue
    if ($null -ne $found) { $TesseractExe = $found.Source }
}
if ([string]::IsNullOrWhiteSpace($TesseractExe)) {
    $candidate = Join-Path $env:ProgramFiles "Tesseract-OCR\tesseract.exe"
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $TesseractExe = $candidate
    }
}
if (-not (Test-Path -LiteralPath $TesseractExe -PathType Leaf)) {
    throw "Tesseract not found; install with winget install -e --id UB-Mannheim.TesseractOCR"
}

$installedTessdata = Join-Path (Split-Path -Parent $TesseractExe) "tessdata"
$englishData = Join-Path $installedTessdata "eng.traineddata"
if (-not (Test-Path -LiteralPath $englishData -PathType Leaf)) {
    throw "English traineddata not found: $englishData"
}
$resolvedTarget = [System.IO.Path]::GetFullPath($TargetDirectory)
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "BabyAI"))
if (-not $resolvedTarget.StartsWith(
        ($allowedRoot.TrimEnd([char]'\') + "\"), [StringComparison]::OrdinalIgnoreCase)) {
    throw "TargetDirectory must be under the user's LOCALAPPDATA\BabyAI directory"
}
New-Item -ItemType Directory -Path $resolvedTarget -Force | Out-Null
$destinationEng = Join-Path $resolvedTarget "eng.traineddata"
$destinationRus = Join-Path $resolvedTarget "rus.traineddata"

Copy-Item -LiteralPath $englishData -Destination $destinationEng -Force
if ((Get-Item -LiteralPath $destinationEng).Length -lt 100000) {
    throw "The installed English traineddata looks incomplete"
}

$source = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/rus.traineddata"
if (-not (Test-Path -LiteralPath $destinationRus -PathType Leaf) -or
    (Get-Item -LiteralPath $destinationRus).Length -lt 300000) {
    $temporaryFile = Join-Path $resolvedTarget "rus.traineddata.download"
    try {
        Write-Host "Downloading official Russian traineddata from tesseract-ocr/tessdata_fast..."
        Invoke-WebRequest -Uri $source -UseBasicParsing -OutFile $temporaryFile
        if ((Get-Item -LiteralPath $temporaryFile).Length -lt 300000) {
            throw "Russian language download seems incomplete"
        }
        Move-Item -LiteralPath $temporaryFile -Destination $destinationRus -Force
    } finally {
        Remove-Item -LiteralPath $temporaryFile -Force -ErrorAction SilentlyContinue
    }
}

$langs = @(& $TesseractExe "--list-langs" "--tessdata-dir" $resolvedTarget 2>&1 |
    ForEach-Object { "$_".Trim().ToLowerInvariant() })
if ($LASTEXITCODE -ne 0 -or $langs -notcontains "eng" -or $langs -notcontains "rus") {
    throw "Tesseract could not load both eng and rus language files in $resolvedTarget"
}
Write-Host "Ready: Russian + English OCR (CPU only)."
Write-Host "Directory: $resolvedTarget"
Write-Host "Run the updated Ginger CPU OCR test. It detects this per-user folder automatically."
