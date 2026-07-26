using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;
using GitKeyRouter.Infrastructure.FileSystem;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class GitProfileServiceTests
{
    [Fact]
    public async Task Preview_GeneratesDirectoryAndRemoteConditionalIncludes()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var profile = Profile();
        var configStore = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitProfiles = [profile],
                GitProfileRules =
                [
                    new GitProfileRule
                    {
                        ProfileId = profile.Id,
                        Kind = GitProfileRuleKind.Directory,
                        Pattern = Path.Combine(temp.Path, "work", "**")
                    },
                    new GitProfileRule
                    {
                        ProfileId = profile.Id,
                        Kind = GitProfileRuleKind.RemoteUrl,
                        Pattern = "https://gitlab.example/company/**"
                    }
                ]
            }
        };
        var service = CreateService(configStore, paths);

        var preview = await service.BuildPreviewAsync();

        Assert.Contains("[includeIf \"gitdir/i:", preview.MasterConfigText, StringComparison.Ordinal);
        Assert.Contains("[includeIf \"hasconfig:remote.*.url:https://gitlab.example/company/**\"]", preview.MasterConfigText, StringComparison.Ordinal);
        var profileText = Assert.Single(preview.ProfileFiles).Value;
        Assert.Contains("name = \"Camus Work\"", profileText, StringComparison.Ordinal);
        Assert.Contains("email = \"work@example.com\"", profileText, StringComparison.Ordinal);
        Assert.Contains("signingKey = \"ABC123\"", profileText, StringComparison.Ordinal);
        Assert.Contains("gpgSign = true", profileText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_WritesFilesAndRegistersSingleGlobalInclude()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var profile = Profile();
        var store = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitProfiles = [profile],
                GitProfileRules =
                [
                    new GitProfileRule
                    {
                        ProfileId = profile.Id,
                        Kind = GitProfileRuleKind.Directory,
                        Pattern = Path.Combine(temp.Path, "work")
                    }
                ]
            }
        };
        var runner = new GitProfileProcessRunner();
        var service = CreateService(store, paths, runner);
        var preview = await service.BuildPreviewAsync();

        var result = await service.ApplyAsync(preview);

        Assert.True(result.Success);
        Assert.True(File.Exists(service.MasterConfigPath));
        Assert.Single(Directory.GetFiles(service.ProfilesDirectory, "profile-*.gitconfig"));
        Assert.Contains(runner.Requests, request => request.Arguments.SequenceEqual(["config", "--global", "--add", "include.path", service.MasterConfigPath.Replace('\\', '/')]));
        Assert.Equal([service.MasterConfigPath.Replace('\\', '/')], runner.IncludePaths);
        Assert.Empty(Directory.EnumerateDirectories(service.TransactionRootDirectory));
    }

    [Fact]
    public async Task Recovery_RestoresInterruptedApplyingTransactionFromPersistentJournal()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var runner = new GitProfileProcessRunner();
        runner.IncludePaths.Add("C:/current/profiles.gitconfig");
        var service = CreateService(new InMemoryAppConfigStore(), paths, runner);
        Directory.CreateDirectory(service.ProfilesDirectory);
        var profilePath = Path.Combine(service.ProfilesDirectory, "profile-work.gitconfig");
        var newlyCreatedPath = Path.Combine(service.ProfilesDirectory, "profile-new.gitconfig");
        await File.WriteAllTextAsync(service.MasterConfigPath, "new master");
        await File.WriteAllTextAsync(profilePath, "new profile");
        await File.WriteAllTextAsync(newlyCreatedPath, "newly created");
        await WriteTransactionAsync(
            service,
            "applying",
            [
                new JournalFile(service.MasterConfigPath, true, "old master"),
                new JournalFile(profilePath, true, "old profile"),
                new JournalFile(newlyCreatedPath, false, null)
            ],
            ["C:/existing/base.gitconfig"]);

        var result = await service.RecoverInterruptedTransactionsAsync();

        Assert.True(result.Success);
        Assert.Equal("old master", await File.ReadAllTextAsync(service.MasterConfigPath));
        Assert.Equal("old profile", await File.ReadAllTextAsync(profilePath));
        Assert.False(File.Exists(newlyCreatedPath));
        Assert.Equal(["C:/existing/base.gitconfig"], runner.IncludePaths);
        Assert.Empty(Directory.EnumerateDirectories(service.TransactionRootDirectory));
    }

    [Fact]
    public async Task Recovery_RejectsTamperedJournalWithoutChangingCurrentFilesOrIncludes()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var runner = new GitProfileProcessRunner();
        runner.IncludePaths.Add("C:/current/profiles.gitconfig");
        var service = CreateService(new InMemoryAppConfigStore(), paths, runner);
        Directory.CreateDirectory(service.ProfilesDirectory);
        await File.WriteAllTextAsync(service.MasterConfigPath, "current master");
        var transactionDirectory = await WriteTransactionAsync(
            service,
            "applying",
            [new JournalFile(service.MasterConfigPath, true, "old master", "BAD-SHA256")],
            ["C:/existing/base.gitconfig"]);

        var result = await service.RecoverInterruptedTransactionsAsync();

        Assert.False(result.Success);
        Assert.Contains("could not be validated", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("current master", await File.ReadAllTextAsync(service.MasterConfigPath));
        Assert.Equal(["C:/current/profiles.gitconfig"], runner.IncludePaths);
        Assert.True(Directory.Exists(transactionDirectory));
    }

    [Fact]
    public async Task RecoveryFailureKeepsApplyingJournalForNextStartupRetry()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var runner = new GitProfileProcessRunner { FailAllIncludeAdds = true };
        runner.IncludePaths.Add("C:/current/profiles.gitconfig");
        var service = CreateService(new InMemoryAppConfigStore(), paths, runner);
        Directory.CreateDirectory(service.ProfilesDirectory);
        await File.WriteAllTextAsync(service.MasterConfigPath, "current master");
        var transactionDirectory = await WriteTransactionAsync(
            service,
            "applying",
            [new JournalFile(service.MasterConfigPath, true, "old master")],
            ["C:/existing/base.gitconfig"]);

        var result = await service.RecoverInterruptedTransactionsAsync();

        Assert.False(result.Success);
        Assert.True(Directory.Exists(transactionDirectory));
        var journal = await File.ReadAllTextAsync(Path.Combine(transactionDirectory, "transaction.json"));
        Assert.Contains("\"State\":\"applying\"", journal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_RestoresFilesAndGlobalIncludesWhenRegistrationFails()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var profile = Profile();
        var store = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitProfiles = [profile],
                GitProfileRules =
                [
                    new GitProfileRule
                    {
                        ProfileId = profile.Id,
                        Kind = GitProfileRuleKind.Directory,
                        Pattern = Path.Combine(temp.Path, "work")
                    }
                ]
            }
        };
        var runner = new GitProfileProcessRunner
        {
            FailNextProfileRegistration = true
        };
        runner.IncludePaths.Add("C:/Users/test/existing.gitconfig");
        var service = CreateService(store, paths, runner);
        Directory.CreateDirectory(service.ProfilesDirectory);
        await File.WriteAllTextAsync(service.MasterConfigPath, "old master");
        var profilePath = Path.Combine(service.ProfilesDirectory, $"profile-{profile.Id}.gitconfig");
        await File.WriteAllTextAsync(profilePath, "old profile");
        var obsoletePath = Path.Combine(service.ProfilesDirectory, "profile-obsolete.gitconfig");
        await File.WriteAllTextAsync(obsoletePath, "obsolete profile");
        var preview = await service.BuildPreviewAsync();

        var result = await service.ApplyAsync(preview);

        Assert.False(result.Success);
        Assert.Contains("restored automatically", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old master", await File.ReadAllTextAsync(service.MasterConfigPath));
        Assert.Equal("old profile", await File.ReadAllTextAsync(profilePath));
        Assert.Equal("obsolete profile", await File.ReadAllTextAsync(obsoletePath));
        Assert.Equal(["C:/Users/test/existing.gitconfig"], runner.IncludePaths);
    }

    [Fact]
    public async Task Apply_RejectsProfileFileChangedAfterPreview()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var profile = Profile();
        var store = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitProfiles = [profile],
                GitProfileRules =
                [
                    new GitProfileRule
                    {
                        ProfileId = profile.Id,
                        Kind = GitProfileRuleKind.Directory,
                        Pattern = Path.Combine(temp.Path, "work")
                    }
                ]
            }
        };
        var runner = new GitProfileProcessRunner();
        var service = CreateService(store, paths, runner);
        Directory.CreateDirectory(service.ProfilesDirectory);
        var profilePath = Path.Combine(service.ProfilesDirectory, $"profile-{profile.Id}.gitconfig");
        await File.WriteAllTextAsync(profilePath, "old profile");
        var preview = await service.BuildPreviewAsync();
        const string externalEdit = "external profile edit";
        await File.WriteAllTextAsync(profilePath, externalEdit);

        var result = await service.ApplyAsync(preview);

        Assert.False(result.Success);
        Assert.Contains("预览后", result.Message, StringComparison.Ordinal);
        Assert.Equal(externalEdit, await File.ReadAllTextAsync(profilePath));
        Assert.DoesNotContain(runner.Requests, request => request.Arguments.Contains("--add"));
    }

    [Fact]
    public async Task Apply_RejectsManagedProfileCreatedAfterPreview()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var profile = Profile();
        var store = new InMemoryAppConfigStore
        {
            Config = new AppConfig
            {
                GitProfiles = [profile],
                GitProfileRules =
                [
                    new GitProfileRule
                    {
                        ProfileId = profile.Id,
                        Kind = GitProfileRuleKind.Directory,
                        Pattern = Path.Combine(temp.Path, "work")
                    }
                ]
            }
        };
        var service = CreateService(store, paths);
        var preview = await service.BuildPreviewAsync();
        Directory.CreateDirectory(service.ProfilesDirectory);
        var unexpectedPath = Path.Combine(service.ProfilesDirectory, "profile-external.gitconfig");
        const string externalContent = "external profile";
        await File.WriteAllTextAsync(unexpectedPath, externalContent);

        var result = await service.ApplyAsync(preview);

        Assert.False(result.Success);
        Assert.Contains("预览后", result.Message, StringComparison.Ordinal);
        Assert.Equal(externalContent, await File.ReadAllTextAsync(unexpectedPath));
    }

    [Fact]
    public void ResolveProfile_UsesLongestDirectoryRuleThenRemoteRule()
    {
        using var temp = new TemporaryDirectory();
        var work = Profile("work", "Work");
        var deep = Profile("deep", "Deep");
        var remote = Profile("remote", "Remote");
        var config = new AppConfig
        {
            GitProfiles = [work, deep, remote],
            GitProfileRules =
            [
                new GitProfileRule { ProfileId = work.Id, Kind = GitProfileRuleKind.Directory, Pattern = temp.Path },
                new GitProfileRule { ProfileId = deep.Id, Kind = GitProfileRuleKind.Directory, Pattern = Path.Combine(temp.Path, "deep") },
                new GitProfileRule { ProfileId = remote.Id, Kind = GitProfileRuleKind.RemoteUrl, Pattern = "git@gitlab.example:company/*" }
            ]
        };
        var service = CreateService(new InMemoryAppConfigStore { Config = config }, new TestAppPaths(temp.Path));

        Assert.Equal(deep.Id, service.ResolveProfile(config, Path.Combine(temp.Path, "deep", "repo"))?.Id);
        Assert.Equal(remote.Id, service.ResolveProfile(config, null, ["git@gitlab.example:company/repo.git"])?.Id);
    }

    private static GitProfileService CreateService(
        InMemoryAppConfigStore store,
        TestAppPaths paths,
        IProcessRunner? runner = null)
        => new(
            store,
            new NoOpBackupService(),
            new PhysicalFileSystem(),
            paths,
            runner ?? new GitProfileProcessRunner(),
            new FixedToolchainService("git.exe"));

    private static GitProfile Profile(string id = "work", string name = "Work")
        => new()
        {
            Id = id,
            DisplayName = name,
            UserName = "Camus Work",
            UserEmail = "work@example.com",
            SigningKey = "ABC123",
            EnableCommitSigning = true
        };

    private static async Task<string> WriteTransactionAsync(
        GitProfileService service,
        string state,
        IReadOnlyList<JournalFile> files,
        IReadOnlyList<string> includePaths)
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(service.TransactionRootDirectory, $"transaction-{id}");
        Directory.CreateDirectory(directory);
        var document = new
        {
            SchemaVersion = 1,
            Id = id,
            State = state,
            GitExecutablePath = "git.exe",
            Files = files.Select(file => new
            {
                file.Path,
                file.Exists,
                file.Content,
                Sha256 = file.Sha256 ?? ComputeSha256(file.Content ?? string.Empty)
            }),
            IncludePaths = includePaths
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, "transaction.json"),
            JsonSerializer.Serialize(document));
        return directory;
    }

    private static string ComputeSha256(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private sealed record JournalFile(string Path, bool Exists, string? Content, string? Sha256 = null);

    private sealed class GitProfileProcessRunner : IProcessRunner
    {
        public List<string> IncludePaths { get; } = [];

        public List<ProcessRequest> Requests { get; } = [];

        public bool FailNextProfileRegistration { get; init; }

        public bool FailAllIncludeAdds { get; init; }

        private bool _registrationFailed;

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var arguments = request.Arguments;
            if (arguments.SequenceEqual(["config", "--global", "--get-all", "include.path"]))
            {
                return Task.FromResult(Result(
                    request,
                    IncludePaths.Count == 0 ? 1 : 0,
                    string.Join(Environment.NewLine, IncludePaths)));
            }

            if (arguments.SequenceEqual(["config", "--global", "--unset-all", "include.path"]))
            {
                var exitCode = IncludePaths.Count == 0 ? 1 : 0;
                IncludePaths.Clear();
                return Task.FromResult(Result(request, exitCode));
            }

            if (arguments.Count >= 5
                && arguments[0] == "config"
                && arguments[1] == "--global"
                && arguments[2] == "--add"
                && arguments[3] == "include.path")
            {
                var value = arguments[4];
                if (FailAllIncludeAdds)
                {
                    return Task.FromResult(Result(request, 1, error: "Simulated include add failure."));
                }

                if (FailNextProfileRegistration
                    && !_registrationFailed
                    && value.EndsWith("/profiles.gitconfig", StringComparison.OrdinalIgnoreCase))
                {
                    _registrationFailed = true;
                    return Task.FromResult(Result(request, 1, error: "Simulated registration failure."));
                }

                IncludePaths.Add(value);
                return Task.FromResult(Result(request, 0));
            }

            if (arguments.Count >= 4
                && arguments[0] == "config"
                && arguments[1] == "--file")
            {
                return Task.FromResult(Result(request, 0));
            }

            return Task.FromResult(Result(request, 0));
        }

        private static ProcessResult Result(
            ProcessRequest request,
            int exitCode,
            string output = "",
            string error = "")
            => new()
            {
                ExecutablePath = request.ExecutablePath,
                Arguments = request.Arguments,
                ExitCode = exitCode,
                StandardOutput = output,
                StandardError = error
            };
    }
}
