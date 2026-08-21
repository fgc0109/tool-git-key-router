[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MsiPath,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$ExpectedVersion,
    [Parameter(Mandatory)][ValidateSet('self-contained', 'framework-dependent')][string]$ExpectedFlavor
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$MsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$msi = Get-Item -LiteralPath $MsiPath
if ($msi.Length -le 0) {
    throw "Installer is empty: $MsiPath"
}

$stream = [System.IO.File]::OpenRead($MsiPath)
try {
    $header = [byte[]]::new(8)
    if ($stream.Read($header, 0, $header.Length) -ne $header.Length -or
        [System.BitConverter]::ToString($header) -cne 'D0-CF-11-E0-A1-B1-1A-E1') {
        throw "Installer does not have a valid Windows Installer compound-file header: $MsiPath"
    }
}
finally {
    $stream.Dispose()
}

$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$database = $windowsInstaller.GetType().InvokeMember(
    'OpenDatabase', 'InvokeMethod', $null, $windowsInstaller, @($MsiPath, 0))

function Get-MsiQueryValue {
    param([Parameter(Mandatory)][string]$Query)

    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($Query))
    $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    if ($null -eq $record) { return $null }
    return [string]$record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
}

function Get-MsiQueryValues {
    param([Parameter(Mandatory)][string]$Query)

    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($Query))
    $null = $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $values = [System.Collections.Generic.List[string]]::new()
    while ($true) {
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $record) { break }
        $values.Add([string]$record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1))
    }
    return $values.ToArray()
}

function Assert-Equal {
    param(
        [AllowNull()][object]$Actual,
        [AllowNull()][object]$Expected,
        [Parameter(Mandatory)][string]$Description
    )

    if ([string]$Actual -cne [string]$Expected) {
        throw "$Description is invalid. Expected '$Expected', found '$Actual'."
    }
}

Assert-Equal (Get-MsiQueryValue "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductVersion'") $ExpectedVersion 'MSI ProductVersion'
Assert-Equal (Get-MsiQueryValue "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductName'") 'GitKeyRouter' 'MSI ProductName'
Assert-Equal (Get-MsiQueryValue "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='Manufacturer'") 'project-base-mirror' 'MSI Manufacturer'
Assert-Equal (Get-MsiQueryValue "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='UpgradeCode'") '{8E52C902-B4A2-4635-A1AF-549B3A0CDC21}' 'MSI UpgradeCode'
Assert-Equal (Get-MsiQueryValue "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='INSTALLERFLAVOR'") $ExpectedFlavor 'MSI installer flavor'
Assert-Equal (Get-MsiQueryValue "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='MsiLogging'") 'voicewarmup' 'MSI automatic logging'
Assert-Equal (Get-MsiQueryValue "SELECT ``Directory_Parent`` FROM ``Directory`` WHERE ``Directory``='APPLICATIONFOLDER'") 'ProgramFiles64Folder' 'MSI install-directory parent'
Assert-Equal (Get-MsiQueryValue "SELECT ``Dialog`` FROM ``Dialog`` WHERE ``Dialog``='GitKeyRouterInstallDirDlg'") 'GitKeyRouterInstallDirDlg' 'MSI install-directory dialog'
Assert-Equal (Get-MsiQueryValue "SELECT ``Feature`` FROM ``Feature`` WHERE ``Feature``='DesktopShortcutFeature'") 'DesktopShortcutFeature' 'MSI desktop-shortcut feature'
Assert-Equal (Get-MsiQueryValue "SELECT ``Shortcut`` FROM ``Shortcut`` WHERE ``Shortcut``='DesktopShortcut'") 'DesktopShortcut' 'MSI desktop shortcut'
Assert-Equal (Get-MsiQueryValue "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='INSTALLDESKTOPSHORTCUT'") '1' 'MSI desktop-shortcut default'
Assert-Equal (Get-MsiQueryValue "SELECT ``Condition`` FROM ``Component`` WHERE ``Component``='DesktopShortcutComponent'") 'INSTALLDESKTOPSHORTCUT = 1' 'MSI desktop-shortcut condition'
Assert-Equal (Get-MsiQueryValue "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ARPPRODUCTICON'") 'GitKeyRouterIcon.exe' 'MSI Programs and Features icon'
Assert-Equal (Get-MsiQueryValue "SELECT ``Name`` FROM ``Icon`` WHERE ``Name``='GitKeyRouterIcon.exe'") 'GitKeyRouterIcon.exe' 'MSI icon resource'
$startMenuShortcutComponent = Get-MsiQueryValue "SELECT ``Component_`` FROM ``Shortcut`` WHERE ``Shortcut``='StartMenuShortcut'"
$startMenuShortcutKeyPath = Get-MsiQueryValue "SELECT ``KeyPath`` FROM ``Component`` WHERE ``Component``='StartMenuShortcutComponent'"
$startMenuShortcutRegistry = Get-MsiQueryValue "SELECT ``Registry`` FROM ``Registry`` WHERE ``Component_``='StartMenuShortcutComponent' AND ``Name``='StartMenuShortcut'"
Assert-Equal $startMenuShortcutComponent 'StartMenuShortcutComponent' 'MSI Start menu shortcut component'
Assert-Equal $startMenuShortcutKeyPath $startMenuShortcutRegistry 'MSI Start menu shortcut key path'
Assert-Equal (Get-MsiQueryValue "SELECT ``Root`` FROM ``Registry`` WHERE ``Registry``='$startMenuShortcutRegistry'") '1' 'MSI Start menu shortcut registry root'
Assert-Equal (Get-MsiQueryValue "SELECT ``Target`` FROM ``Shortcut`` WHERE ``Shortcut``='StartMenuShortcut'") '[APPLICATIONFOLDER]GitKeyRouter.exe' 'MSI Start menu shortcut target'
Assert-Equal (Get-MsiQueryValue "SELECT ``Target`` FROM ``Shortcut`` WHERE ``Shortcut``='DesktopShortcut'") '[APPLICATIONFOLDER]GitKeyRouter.exe' 'MSI desktop shortcut target'
Assert-Equal (Get-MsiQueryValue "SELECT ``Icon_`` FROM ``Shortcut`` WHERE ``Shortcut``='StartMenuShortcut'") '' 'MSI Start menu shortcut icon override'
Assert-Equal (Get-MsiQueryValue "SELECT ``Icon_`` FROM ``Shortcut`` WHERE ``Shortcut``='DesktopShortcut'") '' 'MSI desktop shortcut icon override'
Assert-Equal (Get-MsiQueryValue "SELECT ``Arguments`` FROM ``Shortcut`` WHERE ``Shortcut``='StartMenuShortcut'") '' 'MSI Start menu shortcut arguments'
Assert-Equal (Get-MsiQueryValue "SELECT ``Arguments`` FROM ``Shortcut`` WHERE ``Shortcut``='DesktopShortcut'") '' 'MSI desktop shortcut arguments'
Assert-Equal (Get-MsiQueryValue "SELECT ``Value`` FROM ``Registry`` WHERE ``Name``='InstallerFlavor'") '[INSTALLERFLAVOR]' 'MSI flavor registry marker'
Assert-Equal (Get-MsiQueryValue "SELECT ``Value`` FROM ``Registry`` WHERE ``Name``='InstallLocation'") '[APPLICATIONFOLDER]' 'MSI install-location registry marker'

$mainFile = Get-MsiQueryValue "SELECT ``FileName`` FROM ``File`` WHERE ``File``='GitKeyRouterExe'"
if ($mainFile -notmatch '(?i)GitKeyRouter\.exe$') {
    throw "MSI does not contain GitKeyRouter.exe: $mainFile"
}

$fileNames = @(Get-MsiQueryValues "SELECT ``FileName`` FROM ``File``")
if ($fileNames.Count -le 5) {
    throw "Installer payload must be a multi-file application, found only $($fileNames.Count) files."
}
if (@($fileNames | Where-Object { $_ -match '(?i)GitKeyRouter\.dll$' }).Count -eq 0) {
    throw 'Installer payload does not contain GitKeyRouter.dll.'
}
if (@($fileNames | Where-Object { $_ -match '(?i)GitKeyRouter\.runtimeconfig\.json$' }).Count -eq 0) {
    throw 'Installer payload does not contain GitKeyRouter.runtimeconfig.json.'
}
$updaterFiles = @($fileNames | Where-Object { $_ -match '(?i)GitKeyRouter\.Updater\.exe$' })
if ($updaterFiles.Count -ne 1) {
    throw "Installer payload must contain exactly one GitKeyRouter.Updater.exe, found $($updaterFiles.Count)."
}

$runtimeFiles = @($fileNames | Where-Object { $_ -match '(?i)coreclr\.dll$' })
if ($ExpectedFlavor -ceq 'self-contained' -and $runtimeFiles.Count -eq 0) {
    throw 'Self-contained installer does not include the .NET runtime.'
}
if ($ExpectedFlavor -ceq 'framework-dependent' -and $runtimeFiles.Count -ne 0) {
    throw 'Framework-dependent installer unexpectedly includes the .NET runtime.'
}

$hash = (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash
Write-Host 'Installer validation passed.'
Write-Host "File: $MsiPath"
Write-Host "Flavor: $ExpectedFlavor"
Write-Host "Version: $ExpectedVersion"
Write-Host "Files: $($fileNames.Count)"
Write-Host "Size: $([Math]::Round($msi.Length / 1MB, 2)) MiB"
Write-Host "SHA-256: $hash"
