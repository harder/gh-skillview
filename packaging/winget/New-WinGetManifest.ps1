param(
    [Parameter(Mandatory = $true)][string]$ReleaseRef,
    [Parameter(Mandatory = $true)][string]$AssetsDir,
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [string]$Repository = 'harder/gh-skillview'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($ReleaseRef -cnotmatch '^v(\d+\.\d+\.\d+)$') {
    throw 'WinGet manifests require a stable vMAJOR.MINOR.PATCH release tag.'
}
$version = $Matches[1]
if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'Repository must be an owner/name pair.'
}

$assets = (Resolve-Path -LiteralPath $AssetsDir).Path
$checksums = @{}
foreach ($architecture in @('amd64', 'arm64')) {
    $checksumPath = Join-Path $assets "SHA256SUMS-windows-$architecture.txt"
    foreach ($line in Get-Content -LiteralPath $checksumPath) {
        if ($line -cnotmatch '^([0-9a-fA-F]{64})\s+\*?(.+)$') {
            throw "Invalid checksum line in $checksumPath`: $line"
        }
        if ($checksums.ContainsKey($Matches[2])) {
            throw "Duplicate release checksum for $($Matches[2])"
        }
        $checksums[$Matches[2]] = $Matches[1].ToUpperInvariant()
    }
}

$fileNames = @('skillview-windows-amd64.exe', 'skillview-windows-arm64.exe',
    'gh-skillview-windows-amd64.exe', 'gh-skillview-windows-arm64.exe')
foreach ($name in $fileNames) {
    if (-not $checksums.ContainsKey($name)) {
        throw "Missing release checksum for $name"
    }
    $path = Join-Path $assets $name
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actual -cne $checksums[$name]) {
        throw "Release checksum mismatch for $name"
    }
}

$replacements = @{
    '{{VERSION}}' = $version
    '{{URL_WIN_X64}}' = "https://github.com/$Repository/releases/download/$ReleaseRef/skillview-windows-amd64.exe"
    '{{SHA_WIN_X64}}' = $checksums['skillview-windows-amd64.exe']
    '{{URL_WIN_ARM64}}' = "https://github.com/$Repository/releases/download/$ReleaseRef/skillview-windows-arm64.exe"
    '{{SHA_WIN_ARM64}}' = $checksums['skillview-windows-arm64.exe']
}

$manifestDir = Join-Path $OutputDir "manifests/h/harder/SkillView/$version"
New-Item -ItemType Directory -Path $manifestDir -Force | Out-Null
$templateDir = $PSScriptRoot
foreach ($name in @('harder.SkillView.yaml', 'harder.SkillView.locale.en-US.yaml', 'harder.SkillView.installer.yaml')) {
    $content = Get-Content -LiteralPath (Join-Path $templateDir "$name.tmpl") -Raw
    foreach ($key in $replacements.Keys) {
        $content = $content.Replace($key, $replacements[$key])
    }
    if ($content -match '{{[^}]+}}') {
        throw "Unresolved template value in $name"
    }
    [System.IO.File]::WriteAllText((Join-Path $manifestDir $name), $content,
        [System.Text.UTF8Encoding]::new($false))
}

Write-Host "Generated WinGet manifest set: $manifestDir"
