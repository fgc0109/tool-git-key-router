[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$ReleaseJsonPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$owner = 'project-base-mirror'
$repository = 'tool-git-key-router'
$repositorySlug = "$owner/$repository"
$latestReleaseApi = "https://api.github.com/repos/$repositorySlug/releases/latest"

function Get-LatestRelease {
    if (-not [string]::IsNullOrWhiteSpace($ReleaseJsonPath)) {
        $resolvedFixture = (Resolve-Path -LiteralPath $ReleaseJsonPath).Path
        return (Get-Content -LiteralPath $resolvedFixture -Raw -Encoding UTF8 | ConvertFrom-Json)
    }

    $headers = @{
        'Accept' = 'application/vnd.github+json'
        'User-Agent' = 'GitKeyRouter-UpdateManifest/1.0'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    if (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
        $headers['Authorization'] = "Bearer $($env:GH_TOKEN)"
    }

    return Invoke-RestMethod -Method Get -Uri $latestReleaseApi -Headers $headers
}

function Require-CanonicalAssetUrl {
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string]$Url
    )

    $expected = "https://github.com/$repositorySlug/releases/download/$Tag/$FileName"
    if ($Url -cne $expected) {
        throw "Release asset '$FileName' is not hosted at the canonical URL. Expected '$expected', found '$Url'."
    }
    return $Url
}

$release = Get-LatestRelease
$tag = [string]$release.tag_name
if ($tag -notmatch '^v(?<version>\d+\.\d+\.\d+)$') {
    throw "Latest release tag '$tag' is not a stable vMAJOR.MINOR.PATCH version."
}
if ([bool]$release.draft -or [bool]$release.prerelease) {
    throw "Latest release '$tag' is draft or prerelease and cannot feed the stable update channel."
}

$version = $Matches.version
$expectedReleasePage = "https://github.com/$repositorySlug/releases/tag/$tag"
if ([string]$release.html_url -cne $expectedReleasePage) {
    throw "Latest release page is not canonical. Expected '$expectedReleasePage', found '$($release.html_url)'."
}

$expectedFiles = [ordered]@{
    portableFrameworkDependent = "GitKeyRouter-v$version-win-x64-framework-dependent.zip"
    portableSelfContained = "GitKeyRouter-v$version-win-x64-portable.zip"
    installerFrameworkDependent = "GitKeyRouter-v$version-win-x64-framework-dependent-setup.msi"
    installerSelfContained = "GitKeyRouter-v$version-win-x64-setup.msi"
    checksums = 'SHA256SUMS.txt'
}

$assetUrls = @{}
foreach ($asset in @($release.assets)) {
    $name = [string]$asset.name
    if (-not $expectedFiles.Values.Contains($name)) {
        continue
    }
    if ($assetUrls.ContainsKey($name)) {
        throw "Latest release contains duplicate asset '$name'."
    }
    $assetUrls[$name] = Require-CanonicalAssetUrl -Tag $tag -FileName $name -Url ([string]$asset.browser_download_url)
}

foreach ($fileName in $expectedFiles.Values) {
    if (-not $assetUrls.ContainsKey($fileName)) {
        throw "Latest release '$tag' is missing required update asset '$fileName'."
    }
}

$notes = [string]$release.body
if ($notes.Length -gt 20000) {
    $notes = $notes.Substring(0, 20000)
}

$manifest = [ordered]@{
    schemaVersion = 3
    tagName = $tag
    version = $version
    releasePage = $expectedReleasePage
    downloads = [ordered]@{
        portableFrameworkDependent = $assetUrls[$expectedFiles.portableFrameworkDependent]
        portableSelfContained = $assetUrls[$expectedFiles.portableSelfContained]
        installerFrameworkDependent = $assetUrls[$expectedFiles.installerFrameworkDependent]
        installerSelfContained = $assetUrls[$expectedFiles.installerSelfContained]
    }
    checksumsUrl = $assetUrls[$expectedFiles.checksums]
    notes = $notes
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($fullOutputPath)
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw 'Update manifest output path has no parent directory.'
}
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$tempPath = [System.IO.Path]::Combine(
    $outputDirectory,
    ".$([System.IO.Path]::GetFileName($fullOutputPath)).$([guid]::NewGuid().ToString('N')).tmp")
try {
    $json = $manifest | ConvertTo-Json -Depth 6
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($tempPath, $json + [Environment]::NewLine, $utf8NoBom)
    Move-Item -LiteralPath $tempPath -Destination $fullOutputPath -Force
}
finally {
    if (Test-Path -LiteralPath $tempPath) {
        Remove-Item -LiteralPath $tempPath -Force
    }
}

Write-Host "Generated update manifest for ${tag}: $fullOutputPath"
