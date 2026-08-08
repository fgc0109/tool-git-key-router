using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace GitKeyRouter.App.Updates;

public sealed record UpdateInstallerResult(
    bool Success,
    string? Version,
    int? ExitCode,
    bool RestartRequired,
    string? Message,
    DateTimeOffset CompletedAtUtc);

public sealed class UpdateInstallerLauncher
{
    private const string UpdaterFileName = "GitKeyRouter.Updater.exe";
    private readonly string _stateDirectory;

    public UpdateInstallerLauncher(string stateDirectory)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
    }

    public bool CanInstall(UpdatePackageKind packageKind)
    {
        if (packageKind is not (UpdatePackageKind.InstallerFrameworkDependent or UpdatePackageKind.InstallerSelfContained))
        {
            return false;
        }

        return UpdatePackageDetector.TryGetInstallerRegistration(out var registeredKind, out var installLocation)
            && registeredKind == packageKind
            && CurrentProcessMatchesInstallLocation(installLocation)
            && File.Exists(Path.Combine(installLocation, UpdaterFileName));
    }

    public bool Launch(VerifiedUpdatePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!CanInstall(package.PackageKind))
        {
            return false;
        }

        _ = UpdatePackageDetector.TryGetInstallerRegistration(out _, out var installLocation);
        var trustedUpdater = Path.Combine(installLocation, UpdaterFileName);
        var runnerDirectory = Path.Combine(Path.GetDirectoryName(package.FilePath)!, "runner");
        Directory.CreateDirectory(runnerDirectory);
        var runnerPath = Path.Combine(runnerDirectory, UpdaterFileName);
        File.Copy(trustedUpdater, runnerPath, overwrite: true);
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(File.ReadAllBytes(trustedUpdater)),
                SHA256.HashData(File.ReadAllBytes(runnerPath))))
        {
            File.Delete(runnerPath);
            throw new InvalidDataException("The copied update maintenance program failed integrity verification.");
        }

        Directory.CreateDirectory(_stateDirectory);
        var statePath = Path.Combine(_stateDirectory, "last-update.json");
        var logPath = Path.Combine(_stateDirectory, "installer.log");
        var process = Process.GetCurrentProcess();
        var startUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        var appPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the current application path.");

        var startInfo = new ProcessStartInfo
        {
            FileName = runnerPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Add(startInfo, "--parent-pid", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(startInfo, "--parent-start-utc-ticks", startUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(startInfo, "--msi", Path.GetFullPath(package.FilePath));
        Add(startInfo, "--sha256", package.Sha256);
        Add(startInfo, "--app", Path.GetFullPath(appPath));
        Add(startInfo, "--state", statePath);
        Add(startInfo, "--log", logPath);
        Add(startInfo, "--version", package.Version.ToString());

        return Process.Start(startInfo) is not null;
    }

    public UpdateInstallerResult? TryConsumeResult()
    {
        var statePath = Path.Combine(_stateDirectory, "last-update.json");
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<UpdateInstallerResult>(File.ReadAllText(statePath));
            File.Delete(statePath);
            return result;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new UpdateInstallerResult(
                false,
                null,
                null,
                false,
                $"Could not read the previous update result: {exception.Message}",
                DateTimeOffset.UtcNow);
        }
    }

    private static bool CurrentProcessMatchesInstallLocation(string installLocation)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var expectedPath = Path.Combine(installLocation, "GitKeyRouter.exe");
        return string.Equals(
            Path.GetFullPath(processPath),
            Path.GetFullPath(expectedPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void Add(ProcessStartInfo info, string name, string value)
    {
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(value);
    }
}
