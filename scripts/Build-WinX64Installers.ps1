[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$PayloadRoot,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $root 'src\GitKeyRouter.App\GitKeyRouter.App.csproj'
$installerProject = Join-Path $root 'installer\GitKeyRouter.Installer.wixproj'
$validationScript = Join-Path $PSScriptRoot 'Test-WinX64Installer.ps1'

if ([string]::IsNullOrWhiteSpace($PayloadRoot)) {
    $PayloadRoot = Join-Path $root 'artifacts\installer-payload'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'artifacts\installer'
}

$selfContainedPayload = Join-Path $PayloadRoot 'win-x64-self-contained'
$frameworkPayload = Join-Path $PayloadRoot 'win-x64-framework-dependent'
$selfContainedIntermediate = Join-Path $root 'installer\obj\release-self-contained\'
$frameworkIntermediate = Join-Path $root 'installer\obj\release-framework-dependent\'
$selfContainedName = "GitKeyRouter-v$Version-win-x64-setup.msi"
$frameworkName = "GitKeyRouter-v$Version-win-x64-framework-dependent-setup.msi"
$selfContainedMsi = Join-Path $OutputDirectory $selfContainedName
$frameworkMsi = Join-Path $OutputDirectory $frameworkName
$msiVersion = ($Version -split '-', 2)[0]

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code ${LASTEXITCODE}: dotnet $($Arguments -join ' ')"
    }
}

function Publish-InstallerPayload {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][bool]$SelfContained
    )

    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }

    Invoke-DotNet -Arguments @(
        'publish',
        $appProject,
        '-c', 'Release',
        '-r', 'win-x64',
        '--no-restore',
        "--self-contained=$($SelfContained.ToString().ToLowerInvariant())",
        '-p:UseAppHost=true',
        '-p:PublishSingleFile=false',
        '-p:IncludeNativeLibrariesForSelfExtract=false',
        '-p:PublishTrimmed=false',
        '-p:EnableCompressionInSingleFile=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-p:CopyOutputSymbolsToPublishDirectory=false',
        '-o', $Destination
    )

    $mainExecutable = Join-Path $Destination 'GitKeyRouter.exe'
    if (-not (Test-Path -LiteralPath $mainExecutable -PathType Leaf)) {
        throw "Installer payload did not produce GitKeyRouter.exe: $Destination"
    }
    if (@(Get-ChildItem -LiteralPath $Destination -File -Recurse).Count -le 5) {
        throw "Installer payload is unexpectedly small: $Destination"
    }
}

if (Test-Path -LiteralPath $PayloadRoot) {
    Remove-Item -LiteralPath $PayloadRoot -Recurse -Force
}
if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $PayloadRoot -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Publish-InstallerPayload -Destination $selfContainedPayload -SelfContained $true
Publish-InstallerPayload -Destination $frameworkPayload -SelfContained $false

Invoke-DotNet -Arguments @('restore', $installerProject, '--locked-mode')

Invoke-DotNet -Arguments @(
    'build', $installerProject,
    '-c', 'Release',
    '--no-restore',
    "-p:Version=$msiVersion",
    "-p:PayloadDir=$selfContainedPayload",
    "-p:InstallerOutputName=$([System.IO.Path]::GetFileNameWithoutExtension($selfContainedName))",
    '-p:InstallerFlavor=self-contained',
    "-p:IntermediateOutputPath=$selfContainedIntermediate",
    "-p:OutputPath=$OutputDirectory"
)
if (-not (Test-Path -LiteralPath $selfContainedMsi -PathType Leaf)) {
    throw "Installer build did not produce $selfContainedName."
}

Invoke-DotNet -Arguments @(
    'build', $installerProject,
    '-c', 'Release',
    '--no-restore',
    "-p:Version=$msiVersion",
    "-p:PayloadDir=$frameworkPayload",
    "-p:InstallerOutputName=$([System.IO.Path]::GetFileNameWithoutExtension($frameworkName))",
    '-p:InstallerFlavor=framework-dependent',
    "-p:IntermediateOutputPath=$frameworkIntermediate",
    "-p:OutputPath=$OutputDirectory"
)
if (-not (Test-Path -LiteralPath $frameworkMsi -PathType Leaf)) {
    throw "Installer build did not produce $frameworkName."
}

& $validationScript -MsiPath $selfContainedMsi -ExpectedVersion $msiVersion -ExpectedFlavor 'self-contained'
& $validationScript -MsiPath $frameworkMsi -ExpectedVersion $msiVersion -ExpectedFlavor 'framework-dependent'

Write-Host "Prepared Windows installers in: $OutputDirectory"
Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.msi' | ForEach-Object {
    Write-Host "- $($_.Name) ($($_.Length) bytes)"
}
