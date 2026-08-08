using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitKeyRouter.Updater;

public sealed record UpdateArguments(
    int ParentPid,
    long ParentStartUtcTicks,
    string MsiPath,
    string Sha256,
    string AppPath,
    string StatePath,
    string LogPath,
    string Version)
{
    private static readonly Regex ShaPattern = new("^[A-Fa-f0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static UpdateArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Count % 2 != 0)
        {
            throw new ArgumentException("Updater arguments must be --name value pairs.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index += 2)
        {
            var name = args[index];
            var value = args[index + 1];
            if (!name.StartsWith("--", StringComparison.Ordinal) || !values.TryAdd(name, value))
            {
                throw new ArgumentException($"Invalid or duplicate updater argument '{name}'.");
            }
        }

        var required = new[]
        {
            "--parent-pid", "--parent-start-utc-ticks", "--msi", "--sha256",
            "--app", "--state", "--log", "--version"
        };
        if (values.Count != required.Length || required.Any(name => !values.ContainsKey(name)))
        {
            throw new ArgumentException("Updater arguments are incomplete or contain unknown options.");
        }

        if (!int.TryParse(values["--parent-pid"], NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid) || parentPid <= 0)
        {
            throw new ArgumentException("Invalid parent process ID.");
        }

        if (!long.TryParse(values["--parent-start-utc-ticks"], NumberStyles.None, CultureInfo.InvariantCulture, out var parentStart) || parentStart <= 0)
        {
            throw new ArgumentException("Invalid parent process start time.");
        }

        var sha = values["--sha256"].Trim().ToUpperInvariant();
        if (!ShaPattern.IsMatch(sha))
        {
            throw new ArgumentException("Invalid SHA-256 value.");
        }

        var msi = RequireAbsoluteFilePath(values["--msi"], ".msi");
        var app = RequireAbsoluteFilePath(values["--app"], ".exe");
        var state = RequireAbsoluteFilePath(values["--state"], ".json");
        var log = RequireAbsoluteFilePath(values["--log"], ".log");
        var version = values["--version"].Trim();
        if (!System.Version.TryParse(version, out _))
        {
            throw new ArgumentException("Invalid update version.");
        }

        return new UpdateArguments(parentPid, parentStart, msi, sha, app, state, log, version);
    }

    private static string RequireAbsoluteFilePath(string raw, string expectedExtension)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw))
        {
            throw new ArgumentException("Updater paths must be absolute.");
        }

        var path = Path.GetFullPath(raw);
        if (!string.Equals(Path.GetExtension(path), expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Updater path must use the '{expectedExtension}' extension.");
        }

        return path;
    }
}

public sealed record UpdateResult(
    bool Success,
    string? Version,
    int? ExitCode,
    bool RestartRequired,
    string? Message,
    DateTimeOffset CompletedAtUtc);

public static class UpdateRunner
{
    private static readonly TimeSpan ParentWaitTimeout = TimeSpan.FromMinutes(5);

    public static async Task<int> RunAsync(UpdateArguments arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            await WaitForExactParentAsync(arguments, cancellationToken).ConfigureAwait(false);
            VerifyInstaller(arguments);
            Directory.CreateDirectory(Path.GetDirectoryName(arguments.LogPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(arguments.StatePath)!);

            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var msiexec = Path.Combine(systemDirectory, "msiexec.exe");
            if (!File.Exists(msiexec))
            {
                throw new FileNotFoundException("Windows Installer was not found.", msiexec);
            }

            var info = new ProcessStartInfo
            {
                FileName = msiexec,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            info.ArgumentList.Add("/i");
            info.ArgumentList.Add(arguments.MsiPath);
            info.ArgumentList.Add("/qn");
            info.ArgumentList.Add("/norestart");
            info.ArgumentList.Add("/l*v");
            info.ArgumentList.Add(arguments.LogPath);

            using var installer = Process.Start(info)
                ?? throw new InvalidOperationException("Could not start Windows Installer.");
            await installer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var success = installer.ExitCode is 0 or 3010;
            var restartRequired = installer.ExitCode == 3010;
            var result = new UpdateResult(
                success,
                arguments.Version,
                installer.ExitCode,
                restartRequired,
                success ? "GitKeyRouter update installed successfully." : "Windows Installer reported a failure.",
                DateTimeOffset.UtcNow);
            WriteResultAtomic(arguments.StatePath, result);

            if (success && File.Exists(arguments.AppPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = arguments.AppPath,
                    UseShellExecute = true
                });
                return 0;
            }

            return installer.ExitCode;
        }
        catch (Exception exception)
        {
            try
            {
                WriteResultAtomic(
                    arguments.StatePath,
                    new UpdateResult(false, arguments.Version, null, false, exception.Message, DateTimeOffset.UtcNow));
            }
            catch
            {
                // The original failure remains the most useful result.
            }

            return 1;
        }
    }

    public static string ComputeSha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task WaitForExactParentAsync(UpdateArguments arguments, CancellationToken cancellationToken)
    {
        Process? parent;
        try
        {
            parent = Process.GetProcessById(arguments.ParentPid);
            if (parent.StartTime.ToUniversalTime().Ticks != arguments.ParentStartUtcTicks)
            {
                return;
            }
        }
        catch (ArgumentException)
        {
            return;
        }

        using (parent)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(ParentWaitTimeout);
            await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
    }

    private static void VerifyInstaller(UpdateArguments arguments)
    {
        if (!File.Exists(arguments.MsiPath))
        {
            throw new FileNotFoundException("The downloaded update installer no longer exists.", arguments.MsiPath);
        }

        var actual = Convert.FromHexString(ComputeSha256(arguments.MsiPath));
        var expected = Convert.FromHexString(arguments.Sha256);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException("The update installer failed SHA-256 verification.");
        }
    }

    private static void WriteResultAtomic(string statePath, UpdateResult result)
    {
        var directory = Path.GetDirectoryName(statePath)
            ?? throw new InvalidOperationException("Update result path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(statePath)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(result));
        File.Move(temp, statePath, overwrite: true);
    }
}

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var arguments = UpdateArguments.Parse(args);
            return await UpdateRunner.RunAsync(arguments).ConfigureAwait(false);
        }
        catch
        {
            return 2;
        }
    }
}
