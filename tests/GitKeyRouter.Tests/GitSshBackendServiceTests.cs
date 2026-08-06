using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;
using GitKeyRouter.Infrastructure.ProcessExecution;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class GitSshBackendServiceTests
{
    [Fact]
    public async Task DefaultGitBackendIsClassifiedAsOpenSsh()
    {
        var fixture = CreateFixture();

        var result = await fixture.Service.InspectAsync();

        Assert.True(result.Success);
        Assert.Equal(GitSshBackendKind.OpenSsh, result.Value?.Kind);
        Assert.Equal("Git local trace probe", result.Value?.Source);
    }

    [Fact]
    public async Task LocalGitTraceDetectsPlinkWhenNoSettingIsVisible()
    {
        var fixture = CreateFixture(probeCommand: @"C:\Tools\plink.exe -P 1 git@127.0.0.1");

        var result = await fixture.Service.InspectAsync();

        Assert.True(result.Success);
        Assert.Equal(GitSshBackendKind.PuttyPlink, result.Value?.Kind);
        Assert.Equal("Git local trace probe", result.Value?.Source);
    }

    [Fact]
    public async Task AutoVariantStillUsesTraceToDetectActualPlinkCommand()
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ssh.variant"] = "auto"
        };
        var fixture = CreateFixture(
            config,
            probeCommand: @"C:\Tools\plink.exe -P 1 git@127.0.0.1");

        var result = await fixture.Service.InspectAsync();

        Assert.True(result.Success);
        Assert.Equal(GitSshBackendKind.PuttyPlink, result.Value?.Kind);
        Assert.DoesNotContain("GIT_SSH_VARIANT", result.Value!.EnvironmentBlockers);
    }

    [Fact]
    public async Task DetectsTortoisePlinkFromCoreSshCommand()
    {
        var fixture = CreateFixture(new Dictionary<string, string>
        {
            ["core.sshCommand"] = @"C:\Program Files\TortoiseGit\bin\TortoiseGitPlink.exe"
        });

        var result = await fixture.Service.InspectAsync();

        Assert.True(result.Success);
        Assert.Equal(GitSshBackendKind.TortoisePlink, result.Value?.Kind);
        Assert.True(result.Value?.CanApplyOpenSshFix);
        Assert.Contains("core.sshCommand", result.Value?.Source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PuttyEnvironmentOverrideIsReportedAsUnfixableBlocker()
    {
        var fixture = CreateFixture(environment: new Dictionary<string, string?>
        {
            ["GIT_SSH_COMMAND"] = null,
            ["GIT_SSH"] = @"C:\Tools\plink.exe",
            ["GIT_SSH_VARIANT"] = "putty"
        });

        var result = await fixture.Service.InspectAsync();

        Assert.True(result.Success);
        Assert.Equal(GitSshBackendKind.PuttyPlink, result.Value?.Kind);
        Assert.False(result.Value?.CanApplyOpenSshFix);
        Assert.Contains("GIT_SSH", result.Value!.EnvironmentBlockers);
        Assert.Contains("GIT_SSH_VARIANT", result.Value.EnvironmentBlockers);
    }

    [Fact]
    public async Task ConfirmedFixSetsOpenSshCommandAndVariantThenVerifies()
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["core.sshCommand"] = @"C:\Tools\plink.exe",
            ["ssh.variant"] = "putty"
        };
        var fixture = CreateFixture(config);
        var preview = Assert.IsType<GitSshBackendInspection>((await fixture.Service.InspectAsync()).Value);

        var result = await fixture.Service.UseOpenSshAsync(preview);

        Assert.True(result.Success);
        Assert.Equal(GitSshBackendKind.OpenSsh, result.Value?.After.Kind);
        Assert.Equal("C:/Windows/System32/OpenSSH/ssh.exe", config["core.sshCommand"]);
        Assert.Equal("ssh", config["ssh.variant"]);
        Assert.Contains(fixture.Runner.Requests, request => request.Arguments.SequenceEqual(
            ["config", "--global", "--replace-all", "core.sshCommand", "C:/Windows/System32/OpenSSH/ssh.exe"]));
    }

    [Fact]
    public async Task VariantWriteFailureRestoresBothPreviousGlobalSettings()
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["core.sshCommand"] = @"C:\Tools\plink.exe",
            ["ssh.variant"] = "putty"
        };
        var fixture = CreateFixture(config, failVariantWrite: true);
        var preview = Assert.IsType<GitSshBackendInspection>((await fixture.Service.InspectAsync()).Value);

        var result = await fixture.Service.UseOpenSshAsync(preview);

        Assert.False(result.Success);
        Assert.Equal(@"C:\Tools\plink.exe", config["core.sshCommand"]);
        Assert.Equal("putty", config["ssh.variant"]);
    }

    [Fact]
    public async Task FixRefusesWhenEnvironmentForcesPlink()
    {
        var fixture = CreateFixture(environment: new Dictionary<string, string?>
        {
            ["GIT_SSH_COMMAND"] = @"C:\Tools\plink.exe",
            ["GIT_SSH"] = null,
            ["GIT_SSH_VARIANT"] = null
        });
        var preview = Assert.IsType<GitSshBackendInspection>((await fixture.Service.InspectAsync()).Value);

        var result = await fixture.Service.UseOpenSshAsync(preview);

        Assert.False(result.Success);
        Assert.Contains("environment overrides", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.Arguments.Contains("--replace-all"));
    }

    [Fact]
    public async Task FixNeverReplacesUnknownCustomWrapper()
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["core.sshCommand"] = "company-ssh-wrapper.exe"
        };
        var fixture = CreateFixture(config);
        var preview = Assert.IsType<GitSshBackendInspection>((await fixture.Service.InspectAsync()).Value);

        var result = await fixture.Service.UseOpenSshAsync(preview);

        Assert.False(result.Success);
        Assert.Contains("unknown", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("company-ssh-wrapper.exe", config["core.sshCommand"]);
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.Arguments.Contains("--replace-all"));
    }

    [Fact]
    public async Task RealGitRoundTripUsesIsolatedGlobalConfiguration()
    {
        var gitPath = FindGit();
        if (gitPath is null)
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var globalConfig = Path.Combine(directory.Path, "global.gitconfig");
        await File.WriteAllTextAsync(globalConfig, "[core]\n\tsshCommand = C:/Tools/plink.exe\n[ssh]\n\tvariant = putty\n");
        var environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_GLOBAL"] = globalConfig,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_SSH_COMMAND"] = null,
            ["GIT_SSH"] = null,
            ["GIT_SSH_VARIANT"] = null
        };
        var service = new GitSshBackendService(
            new ProcessRunner(),
            new FixedToolchainService(gitPath, sshPath: "C:/Program Files/OpenSSH/ssh.exe"),
            environment);
        var preview = Assert.IsType<GitSshBackendInspection>((await service.InspectAsync()).Value);

        var applied = await service.UseOpenSshAsync(preview);

        Assert.True(applied.Success);
        Assert.Equal(GitSshBackendKind.OpenSsh, applied.Value?.After.Kind);
        Assert.Equal("\"C:/Program Files/OpenSSH/ssh.exe\"", applied.Value?.CoreSshCommand);
        var text = await File.ReadAllTextAsync(globalConfig);
        Assert.Contains("C:/Program Files/OpenSSH/ssh.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("variant = ssh", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plink", text, StringComparison.OrdinalIgnoreCase);
    }

    private static Fixture CreateFixture(
        Dictionary<string, string>? config = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string probeCommand = "ssh -p 1 git@127.0.0.1",
        bool failVariantWrite = false)
    {
        config ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        environment ??= new Dictionary<string, string?>
        {
            ["GIT_SSH_COMMAND"] = null,
            ["GIT_SSH"] = null,
            ["GIT_SSH_VARIANT"] = null
        };
        var runner = new StubProcessRunner(request =>
        {
            var args = request.Arguments;
            if (args.Count > 0 && args[0] == "ls-remote")
            {
                return Process(128, stderr: $"trace: start_command: {probeCommand}\nConnection refused");
            }

            if (args.SequenceEqual(["config", "--show-origin", "--get", "core.sshCommand"])
                || args.SequenceEqual(["config", "--show-origin", "--get", "ssh.variant"]))
            {
                var key = args[^1];
                return config.TryGetValue(key, out var value)
                    ? Process(0, $"file:C:/Users/test/.gitconfig\t{value}")
                    : Process(1);
            }

            if (args.Count == 4 && args.Take(3).SequenceEqual(["config", "--global", "--get-all"]))
            {
                return config.TryGetValue(args[3], out var value)
                    ? Process(0, value)
                    : Process(1);
            }

            if (args.Count == 5 && args.Take(3).SequenceEqual(["config", "--global", "--replace-all"]))
            {
                if (failVariantWrite && args[3] == "ssh.variant")
                {
                    return Process(1, stderr: "simulated variant write failure");
                }

                config[args[3]] = args[4];
                return Process(0);
            }

            if (args.Count == 4 && args.Take(3).SequenceEqual(["config", "--global", "--unset-all"]))
            {
                config.Remove(args[3]);
                return Process(0);
            }

            if (args.Count == 5 && args.Take(3).SequenceEqual(["config", "--global", "--add"]))
            {
                config[args[3]] = args[4];
                return Process(0);
            }

            throw new InvalidOperationException($"Unexpected Git command: {string.Join(' ', args)}");
        });
        var service = new GitSshBackendService(
            runner,
            new FixedToolchainService(
                "C:/Program Files/Git/cmd/git.exe",
                sshPath: "C:/Windows/System32/OpenSSH/ssh.exe"),
            environment);
        return new Fixture(service, runner);
    }

    private static ProcessResult Process(int exitCode, string stdout = "", string stderr = "")
        => new()
        {
            ExecutablePath = "git.exe",
            ExitCode = exitCode,
            StandardOutput = stdout,
            StandardError = stderr
        };

    private static string? FindGit()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), "git.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private sealed record Fixture(GitSshBackendService Service, StubProcessRunner Runner);
}
