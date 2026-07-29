using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Tests.TestSupport;

internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 7, 18, 8, 30, 45, TimeSpan.Zero);

    public DateTimeOffset LocalNow { get; set; } = new(2026, 7, 18, 16, 30, 45, TimeSpan.FromHours(8));
}

internal sealed class TestAppPaths : IAppPaths
{
    public TestAppPaths(string root)
    {
        AppDataDirectory = System.IO.Path.Combine(root, "appdata");
        ConfigPath = System.IO.Path.Combine(AppDataDirectory, "config.json");
        BackupRootDirectory = System.IO.Path.Combine(AppDataDirectory, "backups");
        UserProfileDirectory = System.IO.Path.Combine(root, "user");
        SshDirectory = System.IO.Path.Combine(UserProfileDirectory, ".ssh");
        SshConfigPath = System.IO.Path.Combine(SshDirectory, "config");
        LegacySshConfigBackupPath = System.IO.Path.Combine(SshDirectory, "config.gitkeyrouter.bak");
    }

    public string AppDataDirectory { get; }
    public string ConfigPath { get; }
    public string BackupRootDirectory { get; }
    public string UserProfileDirectory { get; }
    public string SshDirectory { get; }
    public string SshConfigPath { get; }
    public string LegacySshConfigBackupPath { get; }
}

internal sealed class InMemoryAppConfigStore : IAppConfigStore
{
    private int _revision;

    public string ConfigPath => "memory://config.json";

    public AppConfig Config { get; set; } = new();

    public Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Config);

    public Task<AppConfigSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new AppConfigSnapshot(Config, Version));

    public Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        Config = config;
        _revision++;
        return Task.CompletedTask;
    }

    public Task<AppConfigFileVersion> SaveIfUnchangedAsync(
        AppConfig config,
        AppConfigFileVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (expectedVersion != Version)
        {
            throw new AppConfigConcurrencyException(ConfigPath);
        }

        Config = config;
        _revision++;
        return Task.FromResult(Version);
    }

    public void SimulateExternalChange(AppConfig config)
    {
        Config = config;
        _revision++;
    }

    private AppConfigFileVersion Version => new(true, _revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

internal sealed class NoOpBackupService : IBackupService
{
    public int SnapshotCount { get; private set; }

    public Task<BackupManifest> CreateSnapshotAsync(string reason, CancellationToken cancellationToken = default)
    {
        SnapshotCount++;
        return Task.FromResult(new BackupManifest { Reason = reason, CreatedAt = DateTimeOffset.UtcNow, BackupDirectory = "memory://backup" });
    }

    public Task<IReadOnlyList<BackupManifest>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BackupManifest>>([]);

    public Task<IReadOnlyList<BackupInventoryItem>> InventoryAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BackupInventoryItem>>([]);

    public Task<BackupCleanupPlan> PreviewCleanupAsync(
        IEnumerable<string> backupDirectories,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new BackupCleanupPlan());

    public Task<OperationResult<IReadOnlyList<string>>> CleanAsync(
        BackupCleanupPlan plan,
        CancellationToken cancellationToken = default)
        => Task.FromResult(OperationResult<IReadOnlyList<string>>.Ok([], "Nothing to clean."));

    public Task<BackupSnapshot> ReadAsync(string backupDirectory, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OperationResult> RestoreAppConfigAsync(string backupDirectory, CancellationToken cancellationToken = default)
        => Task.FromResult(OperationResult.Ok());

    public Task<OperationResult> RestoreSshConfigAsync(string backupDirectory, CancellationToken cancellationToken = default)
        => Task.FromResult(OperationResult.Ok());

    public Task<OperationResult> RestoreGitRewritesAsync(string backupDirectory, CancellationToken cancellationToken = default)
        => Task.FromResult(OperationResult.Ok());
}

internal sealed class FakeGitUrlRewriteStore : IGitUrlRewriteStore
{
    public List<GitUrlRewriteRule> Rules { get; } = [];

    public bool FailNextAdd { get; set; }

    public HashSet<int> FailAddAttempts { get; } = [];

    private int _addAttemptCount;

    public ProcessResult? RemoteResult { get; set; }

    public string? GitExecutablePath => "git.exe";

    public Task<IReadOnlyList<GitUrlRewriteRule>> GetAllAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GitUrlRewriteRule>>(Rules.ToList());

    public Task<IReadOnlyList<string>> GetValuesAsync(string configKey, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(Rules
            .Where(item => string.Equals(item.ConfigKey, configKey, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.InsteadOfUrl)
            .ToList());

    public Task<OperationResult<IReadOnlyList<string>>> GetGlobalConfigOriginsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(OperationResult<IReadOnlyList<string>>.Ok(["C:/Users/test/.gitconfig"]));

    public Task<ProcessResult> AddAsync(GitUrlRewriteRule rule, CancellationToken cancellationToken = default)
    {
        _addAttemptCount++;
        if (FailNextAdd || FailAddAttempts.Contains(_addAttemptCount))
        {
            FailNextAdd = false;
            return Task.FromResult(new ProcessResult
            {
                ExecutablePath = "git.exe",
                Arguments = ["config", "--add", rule.ConfigKey, rule.InsteadOfUrl],
                ExitCode = 1,
                StandardError = $"Simulated add failure on attempt {_addAttemptCount}."
            });
        }

        Rules.Add(rule);
        return Task.FromResult(Success("config", "--add", rule.ConfigKey, rule.InsteadOfUrl));
    }

    public Task<ProcessResult> RemoveAllAsync(GitUrlRewriteRule rule, CancellationToken cancellationToken = default)
    {
        Rules.RemoveAll(item => string.Equals(item.BaseUrl, rule.BaseUrl, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.InsteadOfUrl, rule.InsteadOfUrl, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(Success("config", "--unset-all", rule.ConfigKey, rule.InsteadOfUrl));
    }

    public Task<ProcessResult> RemoveAllForKeyAsync(string configKey, CancellationToken cancellationToken = default)
    {
        Rules.RemoveAll(item => string.Equals(item.ConfigKey, configKey, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(Success("config", "--unset-all", configKey));
    }

    public Task<ProcessResult> TestRemoteAsync(string originalUrl, CancellationToken cancellationToken = default)
        => Task.FromResult(RemoteResult ?? Success("ls-remote", originalUrl, "HEAD"));

    private static ProcessResult Success(params string[] args)
        => new()
        {
            ExecutablePath = "git.exe",
            Arguments = args,
            ExitCode = 0
        };
}

internal sealed class FixedToolchainService : IToolchainService
{
    private readonly string _gitPath;
    private readonly string? _sshKeygenPath;
    private readonly string? _sshPath;
    private readonly string? _ghPath;
    private readonly string? _ghVersion;

    public FixedToolchainService(
        string gitPath,
        string? sshKeygenPath = null,
        string? sshPath = null,
        string? ghPath = null,
        string? ghVersion = "gh version 2.96.0 (test)")
    {
        _gitPath = gitPath;
        _sshKeygenPath = sshKeygenPath;
        _sshPath = sshPath;
        _ghPath = ghPath;
        _ghVersion = ghVersion;
    }

    public Task<ToolchainInfo> InspectAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ToolchainInfo
        {
            Git = new ExecutableInfo { Name = "git.exe", Exists = true, SelectedPath = _gitPath, SelectedSource = "test", Version = "test" },
            Ssh = new ExecutableInfo
            {
                Name = "ssh.exe",
                Exists = !string.IsNullOrWhiteSpace(_sshPath),
                SelectedPath = _sshPath
            },
            SshKeygen = new ExecutableInfo
            {
                Name = "ssh-keygen.exe",
                Exists = !string.IsNullOrWhiteSpace(_sshKeygenPath),
                SelectedPath = _sshKeygenPath
            },
            Gh = new ExecutableInfo
            {
                Name = "gh.exe",
                Exists = !string.IsNullOrWhiteSpace(_ghPath),
                SelectedPath = _ghPath,
                SelectedSource = "test",
                CandidatePaths = string.IsNullOrWhiteSpace(_ghPath) ? [] : [_ghPath],
                Version = _ghVersion
            }
        });
}

internal sealed class StubProcessRunner : IProcessRunner
{
    private readonly Func<ProcessRequest, ProcessResult> _handler;

    public StubProcessRunner(Func<ProcessRequest, ProcessResult> handler)
    {
        _handler = handler;
    }

    public List<ProcessRequest> Requests { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.FromResult(_handler(request));
    }
}
