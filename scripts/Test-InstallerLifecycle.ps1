[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CurrentMsi,
    [string]$PreviousMsi = '',
    [string]$InstallDirectory = '',
    [int]$TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$CurrentMsi = (Resolve-Path -LiteralPath $CurrentMsi).Path
if (-not [string]::IsNullOrWhiteSpace($PreviousMsi)) {
    $PreviousMsi = (Resolve-Path -LiteralPath $PreviousMsi).Path
}
if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    $InstallDirectory = Join-Path $env:ProgramFiles 'GitKeyRouter Lifecycle Test'
}

$runRoot = Join-Path $root "artifacts\installer-lifecycle\$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

function Invoke-Msi {
    param(
        [Parameter(Mandatory)][ValidateSet('Install', 'Uninstall')][string]$Action,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$LogPath
    )

    $verb = if ($Action -eq 'Install') { '/i' } else { '/x' }
    $arguments = @($verb, "`"$Path`"", '/qn', '/norestart', '/l*v', "`"$LogPath`"")
    if ($Action -eq 'Install') {
        $arguments += "APPLICATIONFOLDER=`"$InstallDirectory`""
        $arguments += 'INSTALLDESKTOPSHORTCUT=0'
    }

    $process = Start-Process `
        -FilePath "$env:SystemRoot\System32\msiexec.exe" `
        -ArgumentList $arguments `
        -Wait `
        -PassThru `
        -WindowStyle Hidden
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "$Action failed with MSI exit code $($process.ExitCode). Log: $LogPath"
    }
}

function Test-InstalledApplication {
    $executable = Join-Path $InstallDirectory 'GitKeyRouter.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Installed executable is missing: $executable"
    }

    $stdout = Join-Path $runRoot 'version.stdout.txt'
    $stderr = Join-Path $runRoot 'version.stderr.txt'
    $process = Start-Process `
        -FilePath $executable `
        -ArgumentList '--version' `
        -PassThru `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        if (-not $process.HasExited) { $process.Kill($true) }
        throw "Installed version command timed out after $TimeoutSeconds seconds."
    }
    if ($process.ExitCode -ne 0) {
        throw "Installed version command failed with exit code $($process.ExitCode): $((Get-Content $stderr) -join [Environment]::NewLine)"
    }
    if ([string]::IsNullOrWhiteSpace(((Get-Content $stdout) -join ''))) {
        throw 'Installed version command returned no version.'
    }
}

$installedMsi = ''
try {
    if (-not [string]::IsNullOrWhiteSpace($PreviousMsi)) {
        Invoke-Msi -Action Install -Path $PreviousMsi -LogPath (Join-Path $runRoot 'install-previous.log')
        $installedMsi = $PreviousMsi
        Test-InstalledApplication
    }

    Invoke-Msi -Action Install -Path $CurrentMsi -LogPath (Join-Path $runRoot 'install-current.log')
    $installedMsi = $CurrentMsi
    Test-InstalledApplication

    $registry = Get-ItemProperty -LiteralPath 'HKLM:\Software\project-base-mirror\GitKeyRouter' -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace([string]$registry.InstallerFlavor)) {
        throw 'Installer flavor registry marker was not written.'
    }
    if ([System.IO.Path]::GetFullPath([string]$registry.InstallLocation).TrimEnd('\') -cne
        [System.IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')) {
        throw 'Installer registry location does not match the lifecycle test directory.'
    }

    Invoke-Msi -Action Uninstall -Path $CurrentMsi -LogPath (Join-Path $runRoot 'uninstall-current.log')
    $installedMsi = ''
    if (Test-Path -LiteralPath (Join-Path $InstallDirectory 'GitKeyRouter.exe')) {
        throw 'GitKeyRouter.exe remains after uninstall.'
    }
    $remainingMarker = Get-ItemPropertyValue `
        -LiteralPath 'HKLM:\Software\project-base-mirror\GitKeyRouter' `
        -Name 'InstallerFlavor' `
        -ErrorAction SilentlyContinue
    if (-not [string]::IsNullOrWhiteSpace([string]$remainingMarker)) {
        throw 'Installer registry marker remains after uninstall.'
    }

    [ordered]@{
        status = 'Passed'
        currentMsi = $CurrentMsi
        previousMsi = $PreviousMsi
        installDirectory = $InstallDirectory
        logs = $runRoot
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding utf8
    Write-Host "Installer lifecycle passed. Logs: $runRoot"
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($installedMsi)) {
        try {
            $cleanupName = if ($installedMsi -ceq $CurrentMsi) { 'cleanup-current.log' } else { 'cleanup-previous.log' }
            Invoke-Msi -Action Uninstall -Path $installedMsi -LogPath (Join-Path $runRoot $cleanupName)
        }
        catch {
            Write-Warning $_.Exception.Message
        }
    }
}
