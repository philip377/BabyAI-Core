param(
    [Parameter(Mandatory = $true)][string]$SetupExe,
    [Parameter(Mandatory = $true)][string]$BundleDir,
    [Parameter(Mandatory = $true)][string]$OutputExe
)

$ErrorActionPreference = 'Stop'

$setup = (Resolve-Path $SetupExe).Path
$bundle = (Resolve-Path $BundleDir).Path
$output = [System.IO.Path]::GetFullPath($OutputExe)
$sevenZip = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
$sfx = Join-Path $env:ProgramFiles '7-Zip\7z.sfx'
if (-not (Test-Path $sevenZip -PathType Leaf)) { throw "7z.exe was not found at $sevenZip" }
if (-not (Test-Path $sfx -PathType Leaf)) { throw "7z.sfx was not found at $sfx" }

$work = Join-Path $env:RUNNER_TEMP ("babyai-sfx-" + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'payload'
$payloadBundle = Join-Path $payload 'bundle'
New-Item -ItemType Directory -Force -Path $payloadBundle | Out-Null
Copy-Item $setup (Join-Path $payload 'BabyAI-Setup.exe') -Force
Copy-Item (Join-Path $bundle '*') $payloadBundle -Recurse -Force

$archive = Join-Path $work 'payload.7z'
Push-Location $payload
try {
    & $sevenZip a -t7z $archive '.\*' -mx=9 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "7-Zip exited with code $LASTEXITCODE" }
} finally {
    Pop-Location
}

$config = Join-Path $work 'config.txt'
@'
;!@Install@!UTF-8!
Title="BabyAI Setup"
RunProgram="BabyAI-Setup.exe"
GUIMode="2"
;!@InstallEnd@!
'@ | Set-Content -Path $config -Encoding utf8NoBOM

$parent = Split-Path $output -Parent
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$streams = @(
    [System.IO.File]::OpenRead($sfx),
    [System.IO.File]::OpenRead($config),
    [System.IO.File]::OpenRead($archive)
)
try {
    $target = [System.IO.File]::Create($output)
    try {
        foreach ($stream in $streams) { $stream.CopyTo($target) }
    } finally {
        $target.Dispose()
    }
} finally {
    foreach ($stream in $streams) { $stream.Dispose() }
}

if (-not (Test-Path $output -PathType Leaf)) { throw 'Single EXE installer was not produced.' }
Write-Host "Single EXE installer: $output"
Write-Host "Size: $([math]::Round((Get-Item $output).Length / 1MB, 1)) MB"
Remove-Item $work -Recurse -Force
