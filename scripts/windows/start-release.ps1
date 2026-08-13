param(
    [Parameter(Mandatory = $false)]
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "BabyAI"),

    [Parameter(Mandatory = $false)]
    [string]$ModelPath = ""
)

$ErrorActionPreference = "Stop"

$currentPath = Join-Path $InstallRoot "current.json"
$launchPath = Join-Path $InstallRoot "launch.json"
if (-not (Test-Path $currentPath -PathType Leaf)) {
    throw "BabyAI is not installed under $InstallRoot."
}

$current = Get-Content $currentPath -Raw | ConvertFrom-Json
$versionDir = [string]$current.path
if ([string]::IsNullOrWhiteSpace($versionDir) -or -not (Test-Path $versionDir -PathType Container)) {
    throw "The installed BabyAI version directory is missing."
}

$launch = @{
    provider = "native"
    acceleration = "auto"
    model = ""
}
if (Test-Path $launchPath -PathType Leaf) {
    try {
        $saved = Get-Content $launchPath -Raw | ConvertFrom-Json
        if ($saved.provider) { $launch.provider = [string]$saved.provider }
        if ($saved.acceleration) { $launch.acceleration = [string]$saved.acceleration }
        if ($saved.model) { $launch.model = [string]$saved.model }
    } catch {
        # Keep safe defaults if the saved launch preferences cannot be read.
    }
}
if (-not [string]::IsNullOrWhiteSpace($ModelPath)) {
    $launch.model = (Resolve-Path $ModelPath).Path
    Set-Content $launchPath ($launch | ConvertTo-Json) -Encoding UTF8
}

$appDir = Join-Path $versionDir "app"
$exe = Join-Path $appDir "BabyAI.Desktop.exe"
$python = Join-Path $versionDir "python\Scripts\python.exe"
$cpuRuntime = Join-Path $versionDir "runtime\cpu\babyai_native.dll"
$vulkanRuntime = Join-Path $versionDir "runtime\vulkan\babyai_native.dll"

foreach ($required in @($exe, $python, $cpuRuntime)) {
    if (-not (Test-Path $required -PathType Leaf)) {
        throw "Installed BabyAI file is missing: $required"
    }
}

$env:BABYAI_PYTHON = $python
$env:BABYAI_PROVIDER = [string]$launch.provider
$env:BABYAI_NATIVE_ACCELERATION = [string]$launch.acceleration
$env:BABYAI_NATIVE_RUNTIME = $cpuRuntime
if (Test-Path $vulkanRuntime -PathType Leaf) {
    $env:BABYAI_NATIVE_VULKAN_RUNTIME = $vulkanRuntime
}
if (-not [string]::IsNullOrWhiteSpace([string]$launch.model)) {
    $env:BABYAI_NATIVE_MODEL = [string]$launch.model
}

Start-Process -FilePath $exe -WorkingDirectory $appDir
