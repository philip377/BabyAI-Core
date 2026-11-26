# Start a LOCAL multimodal llama.cpp server for UNIX/Ginger visual pilot.
# This script is manually launched by the owner. It does not grant UNIX
# mouse access, change Ginger data, or interact with Ginger/SQL.
# A HuggingFace repo option may download model weights on first use.
[CmdletBinding()]
param(
    [string]$LlamaServer = "llama-server.exe",
    [string]$ModelRepo = "ggml-org/Qwen2.5-VL-3B-Instruct-GGUF:Q4_K_M",
    [string]$LocalModel = "",
    [string]$Mmproj = "",
    [ValidateRange(1024, 65535)][int]$Port = 8080,
    [ValidateRange(0, 80)][int]$GpuLayers = 12,
    [switch]$CpuVisionProjector
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ($env:OS -ne "Windows_NT") {
    throw "Local Ginger vision server must be launched on Windows."
}

$executable = Get-Command -Name $LlamaServer -ErrorAction SilentlyContinue
if ($null -eq $executable) {
    throw "llama-server.exe not found. Install llama.cpp Windows binaries or pass -LlamaServer with its full path."
}
if ($LocalModel.Trim() -and -not (Test-Path -LiteralPath $LocalModel -PathType Leaf)) {
    throw "LocalModel not found: $LocalModel"
}
if ($Mmproj.Trim() -and -not $LocalModel.Trim()) {
    throw "Specify -LocalModel together with -Mmproj."
}
if ($LocalModel.Trim() -and (-not $Mmproj.Trim() -or -not (Test-Path -LiteralPath $Mmproj -PathType Leaf))) {
    throw "Local multimodal model requires its existing -Mmproj file."
}

$argsList = @(
    "--host", "127.0.0.1",
    "--port", [string]$Port,
    "--alias", "local-vision",
    "-c", "4096",
    "-ngl", [string]$GpuLayers,
    "--parallel", "1"
)

if ($LocalModel.Trim()) {
    $argsList += @("-m", (Resolve-Path -LiteralPath $LocalModel).Path)
    $argsList += @("--mmproj", (Resolve-Path -LiteralPath $Mmproj).Path)
} else {
    # Note: -hf may download model weights. This is requested explicitly
    # by running this script and is not performed by UNIX itself.
    $argsList += @("-hf", $ModelRepo)
}
if ($CpuVisionProjector) {
    $argsList += "--no-mmproj-offload"
}
Write-Host "Starting LOCAL vision server at http://127.0.0.1:$Port"
Write-Host "This separate console stays open while you test Ginger vision."
Write-Host "On first start, the model source may download several GB of weights."
Write-Host "Stop the server with Ctrl+C."
& $executable.Source @argsList
if ($LASTEXITCODE -ne 0) {
    throw "llama-server.exe exited with code $LASTEXITCODE"
}
