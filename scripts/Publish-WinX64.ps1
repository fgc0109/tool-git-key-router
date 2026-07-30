[CmdletBinding()]
param(
    [switch]$SkipFormat,
    [switch]$SkipTests,
    [switch]$SkipPublishedAppSmokeTest,
    [switch]$SkipReleaseAssets,
    [switch]$OpenOutput,

    [ValidateSet('All', 'SelfContained', 'FrameworkDependent')]
    [string]$Variant = 'All'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'GitKeyRouter.sln'
$project = Join-Path $root 'src\GitKeyRouter.App\GitKeyRouter.App.csproj'
$selfContainedPublishDir = Join-Path $root 'artifacts\publish\win-x64'
$frameworkDependentPublishDir = Join-Path $root 'artifacts\publish\win-x64-framework-dependent'
$releaseOutputDir = Join-Path $root 'artifacts\release'
$validationScript = Join-Path $PSScriptRoot 'Test-WinX64Publish.ps1'
$installerScript = Join-Path $PSScriptRoot 'Build-WinX64Installers.ps1'
$releaseScript = Join-Path $PSScriptRoot 'Prepare-ReleaseAssets.ps1'
$versionProps = Join-Path $root 'Directory.Build.props'

[xml]$props = Get-Content -LiteralPath $versionProps -Raw
$version = [string]$props.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Version was not found in: $versionProps"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK was not found. This script will not install it automatically.'
}

Write-Host 'GitKeyRouter Windows x64 publish'
Write-Host "Repository root: $root"
Write-Host "Publish root: $(Join-Path $root 'artifacts\publish')"
if ($Variant -eq 'All' -and -not $SkipReleaseAssets) {
    Write-Host "Release assets: $releaseOutputDir"
}
Write-Host ''

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code ${LASTEXITCODE}: dotnet $($Arguments -join ' ')"
    }
}

function Publish-Variant {
    param(
        [Parameter(Mandatory)][string]$Profile,
        [Parameter(Mandatory)][string]$PublishDir,
        [Parameter(Mandatory)][string]$ChecksumFileName
    )

    if (Test-Path -LiteralPath $PublishDir) {
        Remove-Item -LiteralPath $PublishDir -Recurse -Force
    }

    Invoke-DotNet -Arguments @(
        'publish',
        $project,
        '-c', 'Release',
        '-r', 'win-x64',
        '--no-restore',
        "-p:PublishProfile=$Profile",
        '-o', $PublishDir
    )

    $validationParameters = @{
        PublishDir = $PublishDir
        ChecksumFileName = $ChecksumFileName
    }

    if ($SkipPublishedAppSmokeTest) {
        $validationParameters.SkipLaunch = $true
    }

    & $validationScript @validationParameters
    Write-Host "Published $Profile to: $PublishDir"
}

Push-Location $root
try {
    Invoke-DotNet -Arguments @('restore', $solution, '--locked-mode')
    Invoke-DotNet -Arguments @(
        'restore',
        $project,
        '-r', 'win-x64',
        '--locked-mode',
        '-p:NuGetLockFilePath=packages.publish-win-x64.lock.json',
        '-p:PublishSingleFile=true'
    )

    if (-not $SkipFormat) {
        Invoke-DotNet -Arguments @('format', $solution, '--no-restore')
    }

    Invoke-DotNet -Arguments @('build', $solution, '-c', 'Release', '--no-restore')

    if (-not $SkipTests) {
        Invoke-DotNet -Arguments @('test', $solution, '-c', 'Release', '--no-build', '--no-restore')
    }

    if ($Variant -in @('All', 'SelfContained')) {
        Publish-Variant `
            -Profile 'win-x64-single-file' `
            -PublishDir $selfContainedPublishDir `
            -ChecksumFileName 'GitKeyRouter-win-x64.sha256'
    }

    if ($Variant -in @('All', 'FrameworkDependent')) {
        Publish-Variant `
            -Profile 'win-x64-framework-dependent' `
            -PublishDir $frameworkDependentPublishDir `
            -ChecksumFileName 'GitKeyRouter-win-x64-framework-dependent.sha256'
    }

    if ($Variant -eq 'All' -and -not $SkipReleaseAssets) {
        & $installerScript -Version $version
        if ($LASTEXITCODE -ne 0) {
            throw "Installer build failed with exit code $LASTEXITCODE."
        }

        & $releaseScript -Version $version
        if ($LASTEXITCODE -ne 0) {
            throw "Release asset preparation failed with exit code $LASTEXITCODE."
        }
    }

    $outputDirectoryToOpen = switch ($Variant) {
        'SelfContained' { $selfContainedPublishDir }
        'FrameworkDependent' { $frameworkDependentPublishDir }
        default {
            if ($SkipReleaseAssets) {
                Join-Path $root 'artifacts\publish'
            }
            else {
                $releaseOutputDir
            }
        }
    }

    if (-not (Test-Path -LiteralPath $outputDirectoryToOpen -PathType Container)) {
        throw "Expected output directory was not created: $outputDirectoryToOpen"
    }

    Write-Host ''
    Write-Host "Publish completed. Variant: $Variant"
    if ($Variant -in @('All', 'SelfContained')) {
        Write-Host "Self-contained binary: $(Join-Path $selfContainedPublishDir 'GitKeyRouter.exe')"
    }
    if ($Variant -in @('All', 'FrameworkDependent')) {
        Write-Host "Framework-dependent binary: $(Join-Path $frameworkDependentPublishDir 'GitKeyRouter.exe')"
    }
    if ($Variant -eq 'All' -and -not $SkipReleaseAssets) {
        Write-Host "Versioned archives, installers, and checksums: $releaseOutputDir"
    }
    Write-Host "Output folder: $outputDirectoryToOpen"

    if ($OpenOutput) {
        try {
            Write-Host "Opening output folder: $outputDirectoryToOpen"
            Start-Process -FilePath 'explorer.exe' -ArgumentList @($outputDirectoryToOpen)
        }
        catch {
            Write-Warning "Publish succeeded, but Explorer could not open the output folder: $($_.Exception.Message)"
        }
    }
}
finally {
    Pop-Location
}
