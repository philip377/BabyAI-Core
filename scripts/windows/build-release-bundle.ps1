param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$DesktopDir,

    [Parameter(Mandatory = $true)]
    [string]$WheelsDir,

    [Parameter(Mandatory = $true)]
    [string]$CpuRuntime,

    [Parameter(Mandatory = $false)]
    [string]$AvxRuntime = "",

    [Parameter(Mandatory = $false)]
    [string]$Avx2Runtime = "",

    [Parameter(Mandatory = $true)]
    [string]$VulkanRuntime,

    [Parameter(Mandatory = $false)]
    [string]$PythonRuntimeDir = "",

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "dist-release"
)

$ErrorActionPreference = "Stop"

$DesktopDir = (Resolve-Path $DesktopDir).Path
$WheelsDir = (Resolve-Path $WheelsDir).Path
$CpuRuntime = (Resolve-Path $CpuRuntime).Path
if (-not [string]::IsNullOrWhiteSpace($AvxRuntime)) {
    $AvxRuntime = (Resolve-Path $AvxRuntime).Path
}
if (-not [string]::IsNullOrWhiteSpace($Avx2Runtime)) {
    $Avx2Runtime = (Resolve-Path $Avx2Runtime).Path
}
$VulkanRuntime = (Resolve-Path $VulkanRuntime).Path
if (-not [string]::IsNullOrWhiteSpace($PythonRuntimeDir)) {
    $PythonRuntimeDir = (Resolve-Path $PythonRuntimeDir).Path
}
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

$rootName = "BabyAI-$Version-windows-x64"
$stageRoot = Join-Path $OutputDir $rootName
$zipPath = Join-Path $OutputDir "$rootName.zip"
$zipHashPath = "$zipPath.sha256"

if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
New-Item -ItemType Directory -Force $stageRoot | Out-Null
New-Item -ItemType Directory -Force (Join-Path $stageRoot "runtime\cpu") | Out-Null
if (-not [string]::IsNullOrWhiteSpace($AvxRuntime)) {
    New-Item -ItemType Directory -Force (Join-Path $stageRoot "runtime\cpu-avx") | Out-Null
}
if (-not [string]::IsNullOrWhiteSpace($Avx2Runtime)) {
    New-Item -ItemType Directory -Force (Join-Path $stageRoot "runtime\cpu-avx2") | Out-Null
}
New-Item -ItemType Directory -Force (Join-Path $stageRoot "runtime\vulkan") | Out-Null

Copy-Item $DesktopDir (Join-Path $stageRoot "app") -Recurse
Copy-Item $WheelsDir (Join-Path $stageRoot "wheels") -Recurse
Copy-Item $CpuRuntime (Join-Path $stageRoot "runtime\cpu\babyai_native.dll")
if (-not [string]::IsNullOrWhiteSpace($AvxRuntime)) {
    Copy-Item $AvxRuntime (Join-Path $stageRoot "runtime\cpu-avx\babyai_native.dll")
}
if (-not [string]::IsNullOrWhiteSpace($Avx2Runtime)) {
    Copy-Item $Avx2Runtime (Join-Path $stageRoot "runtime\cpu-avx2\babyai_native.dll")
}
Copy-Item $VulkanRuntime (Join-Path $stageRoot "runtime\vulkan\babyai_native.dll")
Copy-Item "scripts\windows\install-release.ps1" (Join-Path $stageRoot "install.ps1")
Copy-Item "scripts\windows\start-release.ps1" (Join-Path $stageRoot "start.ps1")

$modelManifestSource = "packaging\windows\model.json"
if (-not (Test-Path $modelManifestSource -PathType Leaf)) {
    throw "Production model manifest is missing: $modelManifestSource"
}
Copy-Item $modelManifestSource (Join-Path $stageRoot "model.json")

$pythonIncluded = $false
if (-not [string]::IsNullOrWhiteSpace($PythonRuntimeDir)) {
    $pythonExe = Join-Path $PythonRuntimeDir "python.exe"
    if (-not (Test-Path $pythonExe -PathType Leaf)) {
        throw "Self-contained Python runtime is missing python.exe: $pythonExe"
    }
    Copy-Item $PythonRuntimeDir (Join-Path $stageRoot "python") -Recurse
    $pythonIncluded = $true
}

$runtimes = @("cpu", "vulkan")
if (-not [string]::IsNullOrWhiteSpace($AvxRuntime)) {
    $runtimes += "cpu-avx"
}
if (-not [string]::IsNullOrWhiteSpace($Avx2Runtime)) {
    $runtimes += "cpu-avx2"
}

$manifest = [ordered]@{
    schema = 1
    product = "BabyAI"
    version = $Version
    platform = "windows-x64"
    model_included = $false
    model_manifest = "model.json"
    python_included = $pythonIncluded
    python_layout = if ($pythonIncluded) { "embedded" } else { "external-bootstrap" }
    python_versions = @("3.11", "3.12", "3.13")
    runtimes = $runtimes
} | ConvertTo-Json -Depth 4
Set-Content (Join-Path $stageRoot "release.json") $manifest -Encoding UTF8

$hashLines = Get-ChildItem $stageRoot -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($stageRoot.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
Set-Content (Join-Path $stageRoot "SHA256SUMS.txt") $hashLines -Encoding ASCII

Compress-Archive -Path $stageRoot -DestinationPath $zipPath -CompressionLevel Optimal
$archiveHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content $zipHashPath "$archiveHash  $([IO.Path]::GetFileName($zipPath))" -Encoding ASCII

Write-Host "Release bundle: $zipPath"
Write-Host "SHA256: $archiveHash"
Write-Host "Self-contained Python included: $pythonIncluded"
Write-Host "Production model manifest included: model.json"
Write-Host "CPU runtime tiers: $($runtimes -join ', ')"
