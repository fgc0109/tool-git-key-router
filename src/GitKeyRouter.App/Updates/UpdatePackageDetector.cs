using Microsoft.Win32;

namespace GitKeyRouter.App.Updates;

public enum UpdatePackageKind
{
    PortableFrameworkDependent,
    PortableSelfContained,
    InstallerFrameworkDependent,
    InstallerSelfContained
}

public static class UpdatePackageDetector
{
    internal const string RegistryKeyPath = @"Software\project-base-mirror\GitKeyRouter";

    public static UpdatePackageKind Detect()
    {
        if (TryGetInstallerRegistration(out var registeredKind, out var installLocation))
        {
            var processPath = Environment.ProcessPath;
            var expectedPath = Path.Combine(installLocation, "GitKeyRouter.exe");
            if (!string.IsNullOrWhiteSpace(processPath)
                && string.Equals(Path.GetFullPath(processPath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            {
                return registeredKind;
            }
        }

        return UpdatePackageKind.PortableSelfContained;
    }

    public static bool TryGetInstallerRegistration(
        out UpdatePackageKind packageKind,
        out string installLocation)
    {
        packageKind = UpdatePackageKind.PortableSelfContained;
        installLocation = string.Empty;

        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(RegistryKeyPath, writable: false);
            var installerFlavor = key?.GetValue("InstallerFlavor") as string;
            var registeredLocation = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(installerFlavor)
                || string.IsNullOrWhiteSpace(registeredLocation)
                || !Path.IsPathFullyQualified(registeredLocation))
            {
                return false;
            }

            var parsedKind = FromInstallerFlavor(installerFlavor);
            if (parsedKind is not (UpdatePackageKind.InstallerFrameworkDependent or UpdatePackageKind.InstallerSelfContained))
            {
                return false;
            }

            packageKind = parsedKind;
            installLocation = Path.GetFullPath(registeredLocation);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException
                or IOException
                or ArgumentException
                or NotSupportedException
                or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static UpdatePackageKind FromInstallerFlavor(string installerFlavor) =>
        installerFlavor.Trim().ToLowerInvariant() switch
        {
            "self-contained" => UpdatePackageKind.InstallerSelfContained,
            "framework-dependent" => UpdatePackageKind.InstallerFrameworkDependent,
            _ => UpdatePackageKind.PortableSelfContained
        };

    public static string DisplayName(UpdatePackageKind kind) => kind switch
    {
        UpdatePackageKind.InstallerSelfContained => "安装版（自带 .NET 运行时）",
        UpdatePackageKind.InstallerFrameworkDependent => "安装版（依赖 .NET Desktop Runtime）",
        UpdatePackageKind.PortableFrameworkDependent => "便携版（依赖 .NET Desktop Runtime）",
        _ => "便携版（自带 .NET 运行时）"
    };
}
