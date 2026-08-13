param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$DesktopDir,

    [Parameter(Mandatory = $true)]
    [string]$WheelsDir,

    [Parameter(Mandatory = $true)]
    [string]$CpuRuntime,

    [Parameter(Mandatory = $true)]
    [string]$VulkanRuntime,

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "dist-release"
)

$ErrorActionPreference = "Stop"

$DesktopDir = (Resolve-Path $DesktopDir).Path
$WheelsDir = (Resolve-Path $WheelsDir).Path
$CpuRuntime = (Resolve-Path $CpuRuntime).Path
$VulkanRuntime = (Resolve-Path $VulkanRuntime).Path
$OutputDir = [IO.Path]::GetFullPath($OutputDir)

$rootName = "BabyAI-$Version-windows-x64"
$stageRoot = Join-Path $OutputDir $rootName
$zipPath = Join-Path $OutputDir "$rootName.zip"
$zipHashPath = "$zipPath.sha256"

if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
New-Item -ItemType Directory -Force $stageRoot | Out-Null
New-Item -ItemType Directory -Force (Join-Path $stageRoot "runtime\cpu") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $stageRoot "runtime\vulkan") | Out-Null

Copy-Item $DesktopDir (Join-Path $stageRoot "app") -Recurse
Copy-Item $WheelsDir (Join-Path $stageRoot "wheels") -Recurse
Copy-Item $CpuRuntime (Join-Path $stageRoot "runtime\cpu\babyai_native.dll")
Copy-Item $VulkanRuntime (Join-Path $stageRoot "runtime\vulkan\babyai_native.dll")
Copy-Item "scripts\windows\install-release.ps1" (Join-Path $stageRoot "install.ps1")
Copy-Item "scripts\windows\start-release.ps1" (Join-Path $stageRoot "start.ps1")

$manifest = [ordered]@{
    schema = 1
    product = "BabyAI"
    version = $Version
    platform = "windows-x64"
    model_included = $false
    minimum_python = "3.11"
    runtimes = @("cpu", "vulkan")
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
