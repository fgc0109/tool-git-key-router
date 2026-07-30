[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$PublishRoot,
    [string]$InstallerRoot,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $root 'artifacts\publish'
}

if ([string]::IsNullOrWhiteSpace($InstallerRoot)) {
    $InstallerRoot = Join-Path $root 'artifacts\installer'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'artifacts\release'
}

$licensePath = Join-Path $root 'LICENSE'
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
    throw "License file was not found: $licensePath"
}

$stagingRoot = Join-Path $root 'artifacts\release-staging'
Remove-Item -LiteralPath $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

$variants = @(
    [pscustomobject]@{
        SourceDirectory = 'win-x64'
        AssetSuffix = 'portable'
        RuntimeRequirement = 'This portable build includes the .NET runtime. No separate .NET installation is required.'
    },
    [pscustomobject]@{
        SourceDirectory = 'win-x64-framework-dependent'
        AssetSuffix = 'framework-dependent'
        RuntimeRequirement = 'This compact build requires the .NET 10 Desktop Runtime x64.'
    }
)

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $bytes = $algorithm.ComputeHash($stream)
        return ([System.BitConverter]::ToString($bytes)).Replace('-', '')
    }
    finally {
        $stream.Dispose()
        $algorithm.Dispose()
    }
}

$assets = @()
try {
    foreach ($variant in $variants) {
        $sourceDirectory = Join-Path $PublishRoot $variant.SourceDirectory
        $sourceExecutable = Join-Path $sourceDirectory 'GitKeyRouter.exe'
        if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
            throw "Published executable was not found: $sourceExecutable"
        }

        $sourceEntries = @(Get-ChildItem -LiteralPath $sourceDirectory -Force)
        if ($sourceEntries.Count -ne 1) {
            throw "Expected one published file in $sourceDirectory, found $($sourceEntries.Count)."
        }

        $assetBaseName = "GitKeyRouter-v$Version-win-x64-$($variant.AssetSuffix)"
        $variantStagingDirectory = Join-Path $stagingRoot $assetBaseName
        New-Item -ItemType Directory -Path $variantStagingDirectory -Force | Out-Null

        Copy-Item -LiteralPath $sourceExecutable -Destination (Join-Path $variantStagingDirectory 'GitKeyRouter.exe')
        Copy-Item -LiteralPath $licensePath -Destination (Join-Path $variantStagingDirectory 'LICENSE.txt')

        $readme = @(
            "GitKeyRouter v$Version",
            '',
            $variant.RuntimeRequirement,
            '',
            'Requirements:',
            '- Windows 10 or Windows 11 x64',
            '- Git for Windows',
            '- Windows OpenSSH Client or Git for Windows OpenSSH',
            '',
            'Run GitKeyRouter.exe without arguments to open the graphical interface.',
            'Run GitKeyRouter.exe --version to print the application version.'
        ) -join "`r`n"
        [System.IO.File]::WriteAllText(
            (Join-Path $variantStagingDirectory 'README.txt'),
            "$readme`r`n",
            [System.Text.Encoding]::UTF8)

        $zipPath = Join-Path $OutputDirectory "$assetBaseName.zip"
        Compress-Archive -Path (Join-Path $variantStagingDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
        $assets += Get-Item -LiteralPath $zipPath
    }

    $installerNames = @(
        "GitKeyRouter-v$Version-win-x64-setup.msi",
        "GitKeyRouter-v$Version-win-x64-framework-dependent-setup.msi"
    )
    $installerFiles = @(Get-ChildItem -LiteralPath $InstallerRoot -File)
    $actualInstallerNames = @($installerFiles.Name | Sort-Object)
    $expectedInstallerNames = @($installerNames | Sort-Object)
    if (($actualInstallerNames -join '|') -cne ($expectedInstallerNames -join '|')) {
        throw "Expected exactly two versioned MSI files in ${InstallerRoot}; found: $($actualInstallerNames -join ', ')"
    }

    foreach ($installerName in $installerNames) {
        $sourceInstaller = Join-Path $InstallerRoot $installerName
        $destinationInstaller = Join-Path $OutputDirectory $installerName
        if ((Get-Item -LiteralPath $sourceInstaller).Length -le 0) {
            throw "Installer is empty: $sourceInstaller"
        }
        Copy-Item -LiteralPath $sourceInstaller -Destination $destinationInstaller
        $assets += Get-Item -LiteralPath $destinationInstaller
    }

    $checksumLines = @($assets |
        Sort-Object Name |
        ForEach-Object { "$(Get-Sha256 -Path $_.FullName)  $($_.Name)" })
    $checksumPath = Join-Path $OutputDirectory 'SHA256SUMS.txt'
    [System.IO.File]::WriteAllText(
        $checksumPath,
        (($checksumLines -join "`n") + "`n"),
        [System.Text.Encoding]::ASCII)

    Write-Host "Prepared release assets in: $OutputDirectory"
    Get-ChildItem -LiteralPath $OutputDirectory -File | ForEach-Object {
        Write-Host "- $($_.Name) ($($_.Length) bytes)"
    }
}
finally {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
}
