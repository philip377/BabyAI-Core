function Test-BabyAIReleaseFiles {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BundleRoot
    )

    $BundleRoot = (Resolve-Path $BundleRoot).Path
    $checksumsPath = Join-Path $BundleRoot "SHA256SUMS.txt"
    if (-not (Test-Path $checksumsPath -PathType Leaf)) {
        throw "Release checksums not found: $checksumsPath"
    }

    $bundlePrefix = $BundleRoot.TrimEnd('\') + '\'
    foreach ($line in Get-Content $checksumsPath) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $match = [regex]::Match($line, '^([0-9a-fA-F]{64})  (.+)$')
        if (-not $match.Success) { throw "Invalid release checksum entry." }

        $expected = $match.Groups[1].Value.ToLowerInvariant()
        $relative = $match.Groups[2].Value.Replace('/', '\')
        $filePath = [IO.Path]::GetFullPath((Join-Path $BundleRoot $relative))
        if (-not $filePath.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Release checksum path escapes the bundle."
        }
        if (-not (Test-Path $filePath -PathType Leaf)) {
            throw "Release file is missing: $relative"
        }
        $actual = (Get-FileHash $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "Release checksum mismatch: $relative"
        }
    }

    $manifestPath = Join-Path $BundleRoot "release.json"
    if (-not (Test-Path $manifestPath -PathType Leaf)) {
        throw "Release manifest not found: $manifestPath"
    }
    return Get-Content $manifestPath -Raw | ConvertFrom-Json
}
