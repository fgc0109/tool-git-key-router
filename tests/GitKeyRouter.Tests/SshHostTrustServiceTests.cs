using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;
using GitKeyRouter.Infrastructure.FileSystem;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class SshHostTrustServiceTests
{
    private const string CurrentKeyData = "AAAAC3NzaC1lZDI1NTE5AAAAIH+qYXXxv8cjvsRQndAzzvd8vfteVsw/rE3tCBf3Egg1";
    private const string OtherKeyData = "AAAAC3NzaC1lZDI1NTE5AAAAIAABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4f";
    private const string CurrentFingerprint = "SHA256:5ifizBNE7sl3kbNrkalI3tKO3T0Vd+QIHFN/mHVkLdQ";

    [Fact]
    public async Task PreviewClassifiesMissingHostAsNotTrustedAndShowsFingerprint()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);

        var result = await fixture.Service.BuildPreviewAsync(CreateService());

        Assert.True(result.Success);
        var preview = Assert.IsType<SshHostTrustPreview>(result.Value);
        Assert.Equal(SshHostTrustStatus.NotTrusted, preview.Status);
        Assert.False(preview.FileVersion.Exists);
        var key = Assert.Single(preview.ScannedKeys);
        Assert.Equal("ssh-ed25519", key.KeyType);
        Assert.Equal(CurrentFingerprint, key.Fingerprint);
        Assert.Equal("git.policoil.top", preview.HostIdentifier);
        Assert.Equal(fixture.KnownHostsPath, preview.KnownHostsPath);
    }

    [Fact]
    public async Task TrustCreatesKnownHostsAtomicallyAndVerifiesTheWrittenKey()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);
        var service = CreateService();
        var preview = Assert.IsType<SshHostTrustPreview>(
            (await fixture.Service.BuildPreviewAsync(service)).Value);

        var result = await fixture.Service.TrustAsync(service, preview);

        Assert.True(result.Success);
        var applied = Assert.IsType<SshHostTrustApplyResult>(result.Value);
        Assert.Equal(1, applied.AddedKeyCount);
        Assert.Null(applied.BackupPath);
        Assert.Equal(
            $"git.policoil.top ssh-ed25519 {CurrentKeyData}\n",
            await File.ReadAllTextAsync(fixture.KnownHostsPath));
        Assert.Equal(SshHostTrustStatus.Trusted,
            (await fixture.Service.BuildPreviewAsync(service)).Value?.Status);
    }

    [Fact]
    public async Task TrustPreservesExistingContentAndCreatesUniqueBackup()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.KnownHostsPath)!);
        var original = $"github.com ssh-ed25519 {OtherKeyData}\r\n";
        await File.WriteAllTextAsync(fixture.KnownHostsPath, original);
        var service = CreateService();
        var preview = Assert.IsType<SshHostTrustPreview>(
            (await fixture.Service.BuildPreviewAsync(service)).Value);

        var result = await fixture.Service.TrustAsync(service, preview);

        Assert.True(result.Success);
        var applied = Assert.IsType<SshHostTrustApplyResult>(result.Value);
        Assert.NotNull(applied.BackupPath);
        Assert.Equal(original, await File.ReadAllTextAsync(applied.BackupPath));
        var updated = await File.ReadAllTextAsync(fixture.KnownHostsPath);
        Assert.StartsWith(original, updated, StringComparison.Ordinal);
        Assert.EndsWith($"git.policoil.top ssh-ed25519 {CurrentKeyData}\r\n", updated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictIsReportedAndNeverReplacedAutomatically()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.KnownHostsPath)!);
        var original = $"git.policoil.top ssh-ed25519 {OtherKeyData}{Environment.NewLine}";
        await File.WriteAllTextAsync(fixture.KnownHostsPath, original);
        var service = CreateService();
        var preview = Assert.IsType<SshHostTrustPreview>(
            (await fixture.Service.BuildPreviewAsync(service)).Value);

        var result = await fixture.Service.TrustAsync(service, preview);

        Assert.Equal(SshHostTrustStatus.Conflict, preview.Status);
        Assert.False(result.Success);
        Assert.Contains("conflict", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.KnownHostsPath));
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(fixture.KnownHostsPath)!,
            "known_hosts.gitkeyrouter.*.bak"));
    }

    [Fact]
    public async Task MarkedKnownHostsEntryIsNeverAcceptedForAutomaticTrust()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.KnownHostsPath)!);
        var original = $"@revoked git.policoil.top ssh-ed25519 {CurrentKeyData}{Environment.NewLine}";
        await File.WriteAllTextAsync(fixture.KnownHostsPath, original);

        var result = await fixture.Service.BuildPreviewAsync(CreateService());

        Assert.True(result.Success);
        Assert.Equal(SshHostTrustStatus.Conflict, result.Value?.Status);
        Assert.True(result.Value?.ExistingEntriesContainMarkers);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.KnownHostsPath));
    }

    [Fact]
    public async Task TrustRejectsServerKeyChangeAfterPreview()
    {
        using var directory = new TemporaryDirectory();
        var scanCount = 0;
        var fixture = CreateFixture(directory.Path, () => ++scanCount == 1 ? CurrentKeyData : OtherKeyData);
        var service = CreateService();
        var preview = Assert.IsType<SshHostTrustPreview>(
            (await fixture.Service.BuildPreviewAsync(service)).Value);

        var result = await fixture.Service.TrustAsync(service, preview);

        Assert.False(result.Success);
        Assert.Contains("changed after preview", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.KnownHostsPath));
    }

    [Fact]
    public async Task NonStandardPortUsesBracketedKnownHostsIdentifier()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path, hostIdentifier: "[git.policoil.top]:2222");
        var service = CreateService();
        service.SshPort = 2222;

        var result = await fixture.Service.BuildPreviewAsync(service);

        Assert.True(result.Success);
        Assert.Equal("[git.policoil.top]:2222", result.Value?.HostIdentifier);
        Assert.Contains("2222", fixture.Runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task InvalidScanOutputIsRejectedWithoutCreatingKnownHosts()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path, keyDataProvider: () => "not-base64");

        var result = await fixture.Service.BuildPreviewAsync(CreateService());

        Assert.False(result.Success);
        Assert.Contains("did not return a valid host key", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.KnownHostsPath));
    }

    [Fact]
    public async Task VerificationFailureRemovesNewKnownHostsFile()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path, failKnownHostsLookup: true);
        var service = CreateService();
        var preview = Assert.IsType<SshHostTrustPreview>(
            (await fixture.Service.BuildPreviewAsync(service)).Value);

        var result = await fixture.Service.TrustAsync(service, preview);

        Assert.False(result.Success);
        Assert.Contains("rolled back", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.KnownHostsPath));
    }

    [Fact]
    public async Task VerificationFailureRestoresExistingKnownHostsBytes()
    {
        using var directory = new TemporaryDirectory();
        var fixture = CreateFixture(directory.Path, failKnownHostsLookup: true);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.KnownHostsPath)!);
        var original = $"github.com ssh-ed25519 {OtherKeyData}\r\n";
        await File.WriteAllTextAsync(fixture.KnownHostsPath, original);
        var service = CreateService();
        var preview = Assert.IsType<SshHostTrustPreview>(
            (await fixture.Service.BuildPreviewAsync(service)).Value);

        var result = await fixture.Service.TrustAsync(service, preview);

        Assert.False(result.Success);
        Assert.Equal(original, await File.ReadAllTextAsync(fixture.KnownHostsPath));
        var backup = Assert.Single(Directory.EnumerateFiles(
            Path.GetDirectoryName(fixture.KnownHostsPath)!,
            "known_hosts.gitkeyrouter.*.bak"));
        Assert.Equal(original, await File.ReadAllTextAsync(backup));
    }

    private static Fixture CreateFixture(
        string root,
        Func<string>? keyDataProvider = null,
        string hostIdentifier = "git.policoil.top",
        bool failKnownHostsLookup = false)
    {
        keyDataProvider ??= () => CurrentKeyData;
        var paths = new TestAppPaths(root);
        var toolDirectory = Path.Combine(root, "tools");
        Directory.CreateDirectory(toolDirectory);
        var sshPath = Path.Combine(toolDirectory, "ssh.exe");
        var keygenPath = Path.Combine(toolDirectory, "ssh-keygen.exe");
        var keyscanPath = Path.Combine(toolDirectory, "ssh-keyscan.exe");
        File.WriteAllBytes(sshPath, []);
        File.WriteAllBytes(keygenPath, []);
        File.WriteAllBytes(keyscanPath, []);
        var knownHostsPath = Path.Combine(paths.SshDirectory, "known_hosts");
        var runner = new StubProcessRunner(request =>
        {
            if (string.Equals(request.ExecutablePath, keyscanPath, StringComparison.OrdinalIgnoreCase))
            {
                return Process(request.ExecutablePath, 0,
                    $"{hostIdentifier} ssh-ed25519 {keyDataProvider()}");
            }

            if (request.Arguments.Contains("-F"))
            {
                if (failKnownHostsLookup)
                {
                    return Process(request.ExecutablePath, 1);
                }

                var lookup = request.Arguments[request.Arguments.ToList().IndexOf("-F") + 1];
                if (!File.Exists(knownHostsPath))
                {
                    return Process(request.ExecutablePath, 1);
                }

                var matches = File.ReadLines(knownHostsPath)
                    .Where(line => line.StartsWith(lookup + " ", StringComparison.OrdinalIgnoreCase)
                        || line.StartsWith("@revoked " + lookup + " ", StringComparison.OrdinalIgnoreCase)
                        || line.StartsWith("@cert-authority " + lookup + " ", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return matches.Count == 0
                    ? Process(request.ExecutablePath, 1)
                    : Process(request.ExecutablePath, 0,
                        string.Join(Environment.NewLine, matches.Select((line, index) =>
                            $"# Host {lookup} found: line {index + 1}{Environment.NewLine}{line}")));
            }

            throw new InvalidOperationException($"Unexpected process: {request.ExecutablePath}");
        });
        var trustService = new SshHostTrustService(
            new PhysicalFileSystem(),
            paths,
            runner,
            new FixedToolchainService("git.exe", keygenPath, sshPath),
            new TestClock());
        return new Fixture(trustService, runner, knownHostsPath);
    }

    private static GitServiceInstance CreateService()
        => new()
        {
            Id = "gitea-cloud",
            DisplayName = "Gitea-Cloud",
            ProviderKind = GitProviderKind.Gitea,
            HostName = "git.policoil.top",
            SshPort = 22,
            SshUser = "git",
            WebBaseUrl = "https://git.policoil.top"
        };

    private static ProcessResult Process(string executable, int exitCode, string stdout = "")
        => new()
        {
            ExecutablePath = executable,
            ExitCode = exitCode,
            StandardOutput = stdout
        };

    private sealed record Fixture(
        SshHostTrustService Service,
        StubProcessRunner Runner,
        string KnownHostsPath);
}
