using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Infrastructure.GitHub;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class GitHubCliServiceTests
{
    [Fact]
    public async Task Run_RepositoryOptionRoutesIdentityAndSanitizesEnvironment()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request =>
            IsVerification(request)
                ? Success(request, fixture.Camus.AccountName)
                : Success(request));
        var service = CreateService(fixture, runner);

        var result = await service.RunAsync(
            ["release", "create", "v1.0.0", "-R", "project-base-mirror/tool-storage-browser"],
            explicitIdentity: null,
            workingDirectory: temp.Path);

        Assert.True(result.Success);
        Assert.Equal(fixture.Camus.Id, result.IdentityId);
        Assert.Equal(2, runner.Requests.Count);
        var command = runner.Requests[1];
        Assert.Equal(ProcessIoMode.InheritConsole, command.IoMode);
        Assert.False(command.CreateNoWindow);
        Assert.False(command.IncludeArgumentsInResult);
        Assert.Equal("github.com", command.EnvironmentVariables["GH_HOST"]);
        Assert.Equal(service.GetConfigDirectory(fixture.Camus), command.EnvironmentVariables["GH_CONFIG_DIR"]);
        Assert.Equal(
            "github.com/project-base-mirror/tool-storage-browser",
            command.EnvironmentVariables["GH_REPO"]);
        Assert.Null(command.EnvironmentVariables["GH_TOKEN"]);
        Assert.Null(command.EnvironmentVariables["GITHUB_TOKEN"]);
        Assert.Null(command.EnvironmentVariables["GH_ENTERPRISE_TOKEN"]);
        Assert.Null(command.EnvironmentVariables["GITHUB_ENTERPRISE_TOKEN"]);
        Assert.Null(command.EnvironmentVariables["GIT_DIR"]);
        Assert.Null(command.EnvironmentVariables["GIT_WORK_TREE"]);
        Assert.Null(command.EnvironmentVariables["GIT_COMMON_DIR"]);
        Assert.Null(command.EnvironmentVariables["GIT_SSH"]);
        Assert.Null(command.EnvironmentVariables["GIT_SSH_COMMAND"]);
        Assert.Null(command.EnvironmentVariables["GIT_ASKPASS"]);
        Assert.Null(command.EnvironmentVariables["SSH_ASKPASS"]);
        Assert.Null(command.EnvironmentVariables["GCM_INTERACTIVE"]);
        Assert.Null(command.EnvironmentVariables["GIT_CONFIG_GLOBAL"]);
        Assert.Null(command.EnvironmentVariables["GIT_CONFIG_SYSTEM"]);
        Assert.Null(command.EnvironmentVariables["GIT_CONFIG_NOSYSTEM"]);
        Assert.Null(command.EnvironmentVariables["GIT_CONFIG_COUNT"]);
        Assert.NotNull(result.Receipt);
        Assert.Equal("Exclusive", result.Receipt.LockMode);
        Assert.Equal(fixture.Camus.Id, result.Receipt.IdentityId);
        Assert.Equal("github.com/project-base-mirror/tool-storage-browser", result.Receipt.GitHubRepository);
    }

    [Fact]
    public async Task Run_CurrentRepositoryUsesEffectiveSshHostAlias()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request =>
        {
            if (request.ExecutablePath == "git.exe")
            {
                return GitResponse(
                    request,
                    temp.Path,
                    ["origin"],
                    new Dictionary<string, string>
                    {
                        ["origin"] = "git@camus0109:project-base-mirror/tool-storage-browser.git"
                    });
            }

            return IsVerification(request)
                ? Success(request, fixture.Camus.AccountName)
                : Success(request);
        });
        var service = CreateService(fixture, runner);

        var result = await service.RunAsync(
            ["release", "view"],
            explicitIdentity: null,
            workingDirectory: temp.Path);

        Assert.True(result.Success);
        Assert.Equal(fixture.Camus.Id, result.IdentityId);
        Assert.Equal("gh.exe", runner.Requests[^1].ExecutablePath);
        Assert.Equal(
            "github.com/project-base-mirror/tool-storage-browser",
            runner.Requests[^1].EnvironmentVariables["GH_REPO"]);
    }

    [Theory]
    [InlineData("gh version 2.39.9 (2023-11-14)", "2.39.9")]
    [InlineData("unexpected-version-output", "could not be determined")]
    public async Task Run_RejectsGitHubCliWithoutSafeMultiAccountSupport(
        string versionOutput,
        string expectedMessage)
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request => Success(request));
        var service = new GitHubCliService(
            new InMemoryAppConfigStore { Config = fixture.Config },
            fixture.Paths,
            runner,
            new FixedToolchainService(
                "git.exe",
                ghPath: "gh.exe",
                ghVersion: versionOutput));

        var result = await service.RunAsync(
            ["release", "view", "-R", "project-base-mirror/tool-storage-browser"],
            explicitIdentity: null,
            workingDirectory: temp.Path);

        Assert.False(result.Success);
        Assert.Contains(expectedMessage, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Run_RejectsDuplicateStableIdentityIdsBeforeUsingCredentialDirectory()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        fixture.Other.Id = fixture.Camus.Id;
        var runner = new StubProcessRunner(request => Success(request));
        var service = CreateService(fixture, runner);

        var result = await service.RunAsync(
            ["release", "view", "-R", "project-base-mirror/tool-storage-browser"],
            explicitIdentity: fixture.Camus.HostAlias,
            workingDirectory: temp.Path);

        Assert.False(result.Success);
        Assert.Contains("unique, non-empty stable ID", result.Message);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Run_RejectsRouteMismatchButIgnoresUnselectedRemoteIdentity()
    {
        using var temp = new TemporaryDirectory();
        var mismatchFixture = CreateFixture(temp.Path);
        mismatchFixture.Config.RepositoryRoutes.Single(route =>
            route.Owner == "project-base-mirror").IdentityId = mismatchFixture.Other.Id;
        var mismatchRunner = new StubProcessRunner(request =>
            GitResponse(
                request,
                temp.Path,
                ["origin"],
                new Dictionary<string, string>
                {
                    ["origin"] = "git@camus0109:project-base-mirror/tool-storage-browser.git"
                }));

        var mismatch = await CreateService(mismatchFixture, mismatchRunner).RunAsync(
            ["release", "view"],
            explicitIdentity: null,
            workingDirectory: temp.Path);

        Assert.False(mismatch.Success);
        Assert.Contains("uses SSH HostAlias", mismatch.Message);
        Assert.DoesNotContain(mismatchRunner.Requests, request => request.ExecutablePath == "gh.exe");

        var conflictFixture = CreateFixture(temp.Path);
        var conflictRunner = new StubProcessRunner(request =>
            GitResponse(
                request,
                temp.Path,
                ["origin", "backup"],
                new Dictionary<string, string>
                {
                    ["origin"] = "git@camus0109:project-base-mirror/tool-storage-browser.git",
                    ["backup"] = "git@other-account:other-owner/other-repo.git"
                }));

        var conflict = await CreateService(conflictFixture, conflictRunner).ResolveAsync(
            explicitIdentity: null,
            repositorySelector: null,
            workingDirectory: temp.Path);

        Assert.True(conflict.Success);
        Assert.Contains(conflict.Warnings!, warning => warning.Contains("backup", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("origin", conflict.RemoteName);
        Assert.Equal("branch.remote", conflict.RemoteSelectionSource);
    }

    [Fact]
    public async Task Resolve_BranchPushRemoteWinsAndReportsDecision()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request =>
        {
            if (IsArguments(request, "rev-parse", "--show-toplevel"))
            {
                return Success(request, temp.Path);
            }

            if (IsArguments(request, "remote"))
            {
                return Success(request, "origin\npublish");
            }

            if (IsArguments(request, "symbolic-ref", "--quiet", "--short", "HEAD"))
            {
                return Success(request, "main");
            }

            if (IsArguments(request, "config", "--get", "branch.main.pushRemote"))
            {
                return Success(request, "publish");
            }

            if (IsRemoteUrlRequest(request, "publish"))
            {
                return Success(request, "git@camus0109:project-base-mirror/tool-storage-browser.git");
            }

            if (IsRemoteUrlRequest(request, "origin"))
            {
                return Success(request, "git@other-account:other-owner/other-repo.git");
            }

            return Failure(request);
        });

        var result = await CreateService(fixture, runner).ResolveAsync(
            explicitIdentity: null,
            repositorySelector: null,
            workingDirectory: temp.Path);

        Assert.True(result.Success);
        Assert.Equal("publish", result.RemoteName);
        Assert.Equal("branch.pushRemote", result.RemoteSelectionSource);
        Assert.Equal(fixture.Camus.HostAlias, result.HostAlias);
        Assert.Equal("test", result.GitHubCliSource);
        Assert.Equal(["gh.exe"], result.GitHubCliCandidates);
        Assert.Contains(result.Warnings!, warning => warning.Contains("origin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resolve_RejectsConflictingPushUrlsOnSelectedRemote()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request =>
        {
            if (request.ExecutablePath == "git.exe")
            {
                if (IsArguments(request, "rev-parse", "--show-toplevel"))
                {
                    return Success(request, temp.Path);
                }

                if (IsArguments(request, "remote"))
                {
                    return Success(request, "origin");
                }

                if (IsArguments(request, "symbolic-ref", "--quiet", "--short", "HEAD"))
                {
                    return Success(request, "main");
                }

                if (IsArguments(request, "config", "--get", "branch.main.remote"))
                {
                    return Success(request, "origin");
                }

                if (IsRemoteUrlRequest(request, "origin"))
                {
                    return Success(
                        request,
                        "git@camus0109:project-base-mirror/tool-storage-browser.git\n"
                        + "git@other-account:other-owner/other-repo.git");
                }
            }

            return Failure(request);
        });

        var result = await CreateService(fixture, runner).ResolveAsync(
            explicitIdentity: null,
            repositorySelector: null,
            workingDirectory: temp.Path);

        Assert.False(result.Success);
        Assert.Contains("different GitHub identities or repositories", result.Message);
    }

    [Fact]
    public async Task Resolve_HttpsRemoteFallsBackToUniqueRepositoryRoute()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request => request.ExecutablePath == "git.exe"
            ? GitResponse(
                request,
                temp.Path,
                ["origin"],
                new Dictionary<string, string>
                {
                    ["origin"] = "https://github.com/project-base-mirror/tool-storage-browser.git"
                })
            : Success(request));

        var result = await CreateService(fixture, runner).ResolveAsync(
            explicitIdentity: null,
            repositorySelector: null,
            workingDirectory: temp.Path);

        Assert.True(result.Success);
        Assert.Equal(fixture.Camus.Id, result.IdentityId);
        Assert.Equal("github.com/project-base-mirror/tool-storage-browser", result.GitHubRepository);
    }

    [Fact]
    public async Task Run_BlocksPlaintextTokenAndAccountMutationWithoutExposingSecret()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request => Success(request));
        var service = CreateService(fixture, runner);
        var configDirectory = service.GetConfigDirectory(fixture.Camus);
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(configDirectory, "hosts.yml"),
            "github.com:\n    oauth_token: secret-that-must-not-be-reported\n");

        var plaintext = await service.RunAsync(
            ["release", "view"],
            explicitIdentity: fixture.Camus.HostAlias,
            workingDirectory: temp.Path);

        Assert.False(plaintext.Success);
        Assert.Contains("oauth_token", plaintext.Message);
        Assert.DoesNotContain("secret-that-must-not-be-reported", plaintext.Message);
        Assert.Empty(runner.Requests);

        File.Delete(Path.Combine(configDirectory, "hosts.yml"));
        var mutation = await service.RunAsync(
            ["-R", "project-base-mirror/tool-storage-browser", "auth", "switch"],
            explicitIdentity: null,
            workingDirectory: temp.Path);

        Assert.False(mutation.Success);
        Assert.Equal(3, mutation.ExitCode);
        Assert.Contains("blocked", mutation.Message);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Login_UsesBrowserFlowAndRejectsUnexpectedAccount()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request =>
            IsVerification(request)
                ? Success(request, "wrong-account")
                : Success(request));
        var service = CreateService(fixture, runner);

        var result = await service.LoginAsync(fixture.Camus.HostAlias);

        Assert.False(result.Success);
        Assert.Contains("wrong-account", result.Message);
        Assert.Equal(2, runner.Requests.Count);
        var login = runner.Requests[0];
        Assert.Equal(
            ["auth", "login", "--hostname", "github.com", "--git-protocol", "ssh", "--web", "--skip-ssh-key"],
            login.Arguments);
        Assert.Equal(ProcessIoMode.InheritConsole, login.IoMode);
        Assert.False(login.CreateNoWindow);
        Assert.False(login.IncludeArgumentsInResult);
    }

    [Fact]
    public async Task Status_RegistersManifestOnlyAfterVerifiedAccountAndRejectsDirectoryReuse()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request => IsVerification(request)
            ? Success(request, fixture.Camus.AccountName)
            : Success(request));
        var service = CreateService(fixture, runner);

        var verified = await service.StatusAsync(
            fixture.Camus.HostAlias,
            workingDirectory: null);

        Assert.True(verified.Success);
        var manifestPath = Path.Combine(service.GetConfigDirectory(fixture.Camus), "identity.json");
        Assert.True(File.Exists(manifestPath));
        var manifestText = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains(fixture.Camus.Id, manifestText);
        Assert.Contains(fixture.Camus.AccountName, manifestText);
        Assert.DoesNotContain("oauth_token", manifestText, StringComparison.OrdinalIgnoreCase);

        await File.WriteAllTextAsync(
            manifestPath,
            manifestText.Replace(fixture.Camus.Id, fixture.Other.Id, StringComparison.Ordinal));
        runner.Requests.Clear();

        var reused = await service.StatusAsync(
            fixture.Camus.HostAlias,
            workingDirectory: null);

        Assert.False(reused.Success);
        Assert.Contains("does not match", reused.Message);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Logout_TargetsExpectedAccountAndRemovesOnlyIdentityManifest()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request => IsVerification(request)
            ? Success(request, fixture.Camus.AccountName)
            : Success(request));
        var service = CreateService(fixture, runner);
        var status = await service.StatusAsync(fixture.Camus.Id, workingDirectory: null);
        Assert.True(status.Success);
        var configDirectory = service.GetConfigDirectory(fixture.Camus);
        var manifestPath = Path.Combine(configDirectory, "identity.json");
        Assert.True(File.Exists(manifestPath));
        runner.Requests.Clear();

        var logout = await service.LogoutAsync(fixture.Camus.HostAlias);

        Assert.True(logout.Success);
        Assert.Single(runner.Requests);
        Assert.Equal(
            ["auth", "logout", "--hostname", "github.com", "--user", fixture.Camus.AccountName],
            runner.Requests[0].Arguments);
        Assert.Equal(ProcessIoMode.InheritConsole, runner.Requests[0].IoMode);
        Assert.False(File.Exists(manifestPath));
        Assert.True(Directory.Exists(configDirectory));
    }

    [Fact]
    public async Task StatusAll_ReturnsEveryConfiguredGitHubIdentityWithoutGuessing()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request => IsVerification(request)
            ? Success(request, fixture.Camus.AccountName)
            : Success(request));
        var service = CreateService(fixture, runner);

        var results = await service.StatusAllAsync();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, result => result.IdentityId == fixture.Camus.Id && result.Success);
        Assert.Contains(results, result => result.IdentityId == fixture.Other.Id && !result.Success);
    }

    [Theory]
    [InlineData("github.com:\n    \"oauth_token\": secret-value\n", "oauth_token")]
    [InlineData("github.com:\n    'oauth_token': secret-value\n", "oauth_token")]
    public async Task Status_BlocksQuotedPlaintextTokenKeysWithoutReadingValues(
        string hostsContent,
        string expectedMessage)
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request => Success(request));
        var service = CreateService(fixture, runner);
        var configDirectory = service.GetConfigDirectory(fixture.Camus);
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(Path.Combine(configDirectory, "hosts.yml"), hostsContent);

        var result = await service.StatusAsync(fixture.Camus.Id, workingDirectory: null);

        Assert.False(result.Success);
        Assert.Contains(expectedMessage, result.Message);
        Assert.DoesNotContain("secret-value", result.Message);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Status_RejectsOversizedCredentialMetadataBeforeAccountProbe()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request => Success(request));
        var service = CreateService(fixture, runner);
        var configDirectory = service.GetConfigDirectory(fixture.Camus);
        Directory.CreateDirectory(configDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(configDirectory, "hosts.yml"),
            new string('x', 1024 * 1024 + 1));

        var result = await service.StatusAsync(fixture.Camus.Id, workingDirectory: null);

        Assert.False(result.Success);
        Assert.Contains("safety limit", result.Message);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Run_UsesSharedLockForOrdinaryCommandsAndExclusiveLockForExtensions()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request => IsVerification(request)
            ? Success(request, fixture.Camus.AccountName)
            : Success(request));
        var service = CreateService(fixture, runner);
        Assert.True((await service.StatusAsync(fixture.Camus.Id, workingDirectory: null)).Success);

        var ordinary = await service.RunAsync(
            ["repo", "view"],
            fixture.Camus.Id,
            temp.Path);
        var extension = await service.RunAsync(
            ["extension", "list"],
            fixture.Camus.Id,
            temp.Path);

        Assert.True(ordinary.Success);
        Assert.Equal("Shared", ordinary.Receipt!.LockMode);
        Assert.True(extension.Success);
        Assert.Equal("Exclusive", extension.Receipt!.LockMode);
        Assert.Equal("gh version 2.96.0 (test)", ordinary.Receipt.GitHubCliVersion);
    }

    [Fact]
    public async Task Run_AllowsConcurrentCommandsForTheSameVerifiedIdentity()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new ConcurrentCommandRunner(fixture.Camus.AccountName);
        var service = new GitHubCliService(
            new InMemoryAppConfigStore { Config = fixture.Config },
            fixture.Paths,
            runner,
            new FixedToolchainService("git.exe", ghPath: "gh.exe"));
        Assert.True((await service.StatusAsync(fixture.Camus.Id, workingDirectory: null)).Success);

        var first = service.RunAsync(["repo", "view"], fixture.Camus.Id, temp.Path);
        var second = service.RunAsync(["issue", "list"], fixture.Camus.Id, temp.Path);
        try
        {
            await runner.BothCommandsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            runner.ReleaseCommands.TrySetResult();
        }

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Success));
        Assert.All(results, result => Assert.Equal("Shared", result.Receipt!.LockMode));
        Assert.Equal(2, runner.MaximumConcurrentCommands);
    }

    [Fact]
    public async Task Logout_CannotBypassAnExistingSharedIdentityLock()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new StubProcessRunner(request => IsVerification(request)
            ? Success(request, fixture.Camus.AccountName)
            : Success(request));
        var service = CreateService(fixture, runner);
        Assert.True((await service.StatusAsync(fixture.Camus.Id, workingDirectory: null)).Success);
        var configDirectory = service.GetConfigDirectory(fixture.Camus);
        var lockPath = Path.Combine(configDirectory, ".gitkeyrouter.lock");
        runner.Requests.Clear();

        await using var sharedLock = new FileStream(
            lockPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.LogoutAsync(fixture.Camus.Id, cancellation.Token));
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Run_AllowsDifferentIdentitiesToExecuteConcurrently()
    {
        using var temp = new TemporaryDirectory();
        var fixture = CreateFixture(temp.Path);
        var runner = new ConcurrentCommandRunner(fixture.Camus.AccountName);
        var service = new GitHubCliService(
            new InMemoryAppConfigStore { Config = fixture.Config },
            fixture.Paths,
            runner,
            new FixedToolchainService("git.exe", ghPath: "gh.exe"));
        runner.AccountsByConfigDirectory[service.GetConfigDirectory(fixture.Camus)] = fixture.Camus.AccountName;
        runner.AccountsByConfigDirectory[service.GetConfigDirectory(fixture.Other)] = fixture.Other.AccountName;
        Assert.True((await service.StatusAsync(fixture.Camus.Id, workingDirectory: null)).Success);
        Assert.True((await service.StatusAsync(fixture.Other.Id, workingDirectory: null)).Success);

        var first = service.RunAsync(["repo", "view"], fixture.Camus.Id, temp.Path);
        var second = service.RunAsync(["issue", "list"], fixture.Other.Id, temp.Path);
        try
        {
            await runner.BothCommandsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            runner.ReleaseCommands.TrySetResult();
        }

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(2, runner.MaximumConcurrentCommands);
        Assert.NotEqual(results[0].Receipt!.IdentityId, results[1].Receipt!.IdentityId);
    }

    private static GitHubCliService CreateService(Fixture fixture, StubProcessRunner runner)
        => new(
            new InMemoryAppConfigStore { Config = fixture.Config },
            fixture.Paths,
            runner,
            new FixedToolchainService("git.exe", ghPath: "gh.exe"));

    private static Fixture CreateFixture(string root)
    {
        var github = GitServiceInstance.CreateGitHubCom();
        var camus = new GitIdentity
        {
            Id = "identity-camus",
            ServiceInstanceId = github.Id,
            DisplayName = "Camus",
            AccountName = "camus0109",
            HostAlias = "camus0109"
        };
        var other = new GitIdentity
        {
            Id = "identity-other",
            ServiceInstanceId = github.Id,
            DisplayName = "Other",
            AccountName = "other-account",
            HostAlias = "other-account"
        };
        var config = new AppConfig
        {
            GitServices = [github],
            Identities = [camus, other],
            RepositoryRoutes =
            [
                new RepositoryRoute
                {
                    ServiceInstanceId = github.Id,
                    Scope = GitRouteScope.Repository,
                    Owner = "project-base-mirror",
                    Repository = "tool-storage-browser",
                    IdentityId = camus.Id
                },
                new RepositoryRoute
                {
                    ServiceInstanceId = github.Id,
                    Scope = GitRouteScope.Repository,
                    Owner = "other-owner",
                    Repository = "other-repo",
                    IdentityId = other.Id
                }
            ]
        };
        config.Normalize();
        return new Fixture(config, camus, other, new TestAppPaths(root));
    }

    private static ProcessResult GitResponse(
        ProcessRequest request,
        string repositoryRoot,
        IReadOnlyList<string> remotes,
        IReadOnlyDictionary<string, string> urls)
    {
        if (IsArguments(request, "rev-parse", "--show-toplevel"))
        {
            return Success(request, repositoryRoot);
        }

        if (IsArguments(request, "remote"))
        {
            return Success(request, string.Join(Environment.NewLine, remotes));
        }

        if (IsArguments(request, "symbolic-ref", "--quiet", "--short", "HEAD"))
        {
            return Success(request, "main");
        }

        if (IsArguments(request, "config", "--get", "branch.main.remote"))
        {
            return Success(request, "origin");
        }

        if (request.Arguments.Count == 5
            && request.Arguments[0] == "remote"
            && request.Arguments[1] == "get-url"
            && request.Arguments[2] == "--push"
            && request.Arguments[3] == "--all"
            && urls.TryGetValue(request.Arguments[4], out var url))
        {
            return Success(request, url);
        }

        return Failure(request);
    }

    private static bool IsVerification(ProcessRequest request)
        => IsArguments(request, "api", "user", "--jq", ".login");

    private static bool IsArguments(ProcessRequest request, params string[] expected)
        => request.Arguments.SequenceEqual(expected);

    private static bool IsRemoteUrlRequest(ProcessRequest request, string remote)
        => IsArguments(request, "remote", "get-url", "--push", "--all", remote);

    private static ProcessResult Success(ProcessRequest request, string output = "")
        => new()
        {
            ExecutablePath = request.ExecutablePath,
            Arguments = request.Arguments,
            ExitCode = 0,
            StandardOutput = output
        };

    private static ProcessResult Failure(ProcessRequest request)
        => new()
        {
            ExecutablePath = request.ExecutablePath,
            Arguments = request.Arguments,
            ExitCode = 1
        };

    private sealed record Fixture(
        AppConfig Config,
        GitIdentity Camus,
        GitIdentity Other,
        TestAppPaths Paths);

    private sealed class ConcurrentCommandRunner : IProcessRunner
    {
        private readonly string _accountName;
        private int _activeCommands;
        private int _maximumConcurrentCommands;

        public ConcurrentCommandRunner(string accountName)
        {
            _accountName = accountName;
        }

        public TaskCompletionSource BothCommandsEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseCommands { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Dictionary<string, string> AccountsByConfigDirectory { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public int MaximumConcurrentCommands => Volatile.Read(ref _maximumConcurrentCommands);

        public async Task<ProcessResult> RunAsync(
            ProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            if (IsVerification(request))
            {
                var configDirectory = request.EnvironmentVariables["GH_CONFIG_DIR"];
                var accountName = configDirectory is not null
                    && AccountsByConfigDirectory.TryGetValue(configDirectory, out var configured)
                        ? configured
                        : _accountName;
                return Success(request, accountName);
            }

            var active = Interlocked.Increment(ref _activeCommands);
            UpdateMaximum(active);
            if (active >= 2)
            {
                BothCommandsEntered.TrySetResult();
            }

            try
            {
                await ReleaseCommands.Task.WaitAsync(cancellationToken);
                return new ProcessResult
                {
                    ExecutablePath = request.ExecutablePath,
                    Arguments = request.Arguments,
                    ExitCode = 0,
                    Duration = TimeSpan.FromMilliseconds(25)
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeCommands);
            }
        }

        private void UpdateMaximum(int value)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentCommands);
                if (current >= value
                    || Interlocked.CompareExchange(ref _maximumConcurrentCommands, value, current) == current)
                {
                    return;
                }
            }
        }
    }
}
