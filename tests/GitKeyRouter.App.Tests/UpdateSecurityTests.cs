using System.Net;
using System.Text;
using GitKeyRouter.App.Updates;
using GitKeyRouter.Updater;

namespace GitKeyRouter.App.Tests;

public sealed class UpdateSecurityTests
{
    [Fact]
    public void StableVersionParser_RejectsPrereleaseAndAmbiguousTags()
    {
        Assert.Equal(new Version(1, 2, 3, 0), GitHubUpdateChecker.TryParseStableVersion("v1.2.3"));
        Assert.Equal(new Version(1, 2, 3, 4), GitHubUpdateChecker.TryParseStableVersion("1.2.3.4"));
        Assert.Null(GitHubUpdateChecker.TryParseStableVersion("v1.2"));
        Assert.Null(GitHubUpdateChecker.TryParseStableVersion("v1.2.3-beta.1"));
        Assert.Null(GitHubUpdateChecker.TryParseStableVersion("latest"));
    }

    [Fact]
    public void ManifestSchema3_MapsAllPackagesAndRequiresCanonicalHttps()
    {
        const string json = """
            {
              "schemaVersion": 3,
              "tagName": "v1.2.3",
              "version": "1.2.3",
              "releasePage": "https://github.com/project-base-mirror/tool-git-key-router/releases/tag/v1.2.3",
              "downloads": {
                "portableFrameworkDependent": "https://github.com/project-base-mirror/tool-git-key-router/releases/download/v1.2.3/GitKeyRouter-v1.2.3-win-x64-framework-dependent.zip",
                "portableSelfContained": "https://github.com/project-base-mirror/tool-git-key-router/releases/download/v1.2.3/GitKeyRouter-v1.2.3-win-x64-portable.zip",
                "installerFrameworkDependent": "https://github.com/project-base-mirror/tool-git-key-router/releases/download/v1.2.3/GitKeyRouter-v1.2.3-win-x64-framework-dependent-setup.msi",
                "installerSelfContained": "https://github.com/project-base-mirror/tool-git-key-router/releases/download/v1.2.3/GitKeyRouter-v1.2.3-win-x64-setup.msi"
              },
              "checksumsUrl": "https://github.com/project-base-mirror/tool-git-key-router/releases/download/v1.2.3/SHA256SUMS.txt",
              "notes": "notes"
            }
            """;

        var release = GitHubUpdateChecker.ParseManifest(json);

        Assert.Equal(new Version(1, 2, 3, 0), release.Version);
        Assert.True(release.HasVerifiedInstallerDownload(UpdatePackageKind.InstallerSelfContained));
        Assert.Equal(
            "GitKeyRouter-v1.2.3-win-x64-setup.msi",
            release.PreferredFileName(UpdatePackageKind.InstallerSelfContained));
    }

    [Fact]
    public void Manifest_RejectsHttpAndForeignReleaseAssets()
    {
        const string httpJson = """
            {
              "schemaVersion": 3,
              "tagName": "v1.2.3",
              "releasePage": "http://example.test/v1.2.3",
              "downloads": {}
            }
            """;
        Assert.Throws<InvalidDataException>(() => GitHubUpdateChecker.ParseManifest(httpJson));

        const string foreignJson = """
            {
              "schemaVersion": 3,
              "tagName": "v1.2.3",
              "releasePage": "https://github.com/project-base-mirror/tool-git-key-router/releases/tag/v1.2.3",
              "downloads": {
                "installerSelfContained": "https://example.test/GitKeyRouter-v1.2.3-win-x64-setup.msi"
              }
            }
            """;
        Assert.Throws<InvalidDataException>(() => GitHubUpdateChecker.ParseManifest(foreignJson));
    }

    [Fact]
    public async Task Checker_FallsBackToGitHubApiWhenManifestFails()
    {
        const string githubJson = """
            {
              "tag_name": "v1.2.3",
              "html_url": "https://github.com/project-base-mirror/tool-git-key-router/releases/tag/v1.2.3",
              "draft": false,
              "prerelease": false,
              "body": "fallback",
              "assets": []
            }
            """;
        using var client = new HttpClient(new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(githubJson, Encoding.UTF8, "application/json")
            }));
        var checker = new GitHubUpdateChecker(client);

        var release = await checker.GetLatestAsync();

        Assert.Equal(UpdateReleaseSource.GitHubApi, release.Source);
        Assert.Equal("fallback", release.Notes);
    }

    [Fact]
    public void ChecksumParser_RequiresExactlyOneMatchingEntry()
    {
        const string fileName = "GitKeyRouter-v1.2.3-win-x64-setup.msi";
        var hash = new string('A', 64);
        Assert.Equal(
            hash,
            UpdateDownloadService.ParseExpectedSha256($"{hash}  {fileName}\n", fileName));
        Assert.Throws<InvalidDataException>(() =>
            UpdateDownloadService.ParseExpectedSha256($"{hash}  other.msi\n", fileName));
        Assert.Throws<InvalidDataException>(() =>
            UpdateDownloadService.ParseExpectedSha256(
                $"{hash}  {fileName}\n{new string('B', 64)}  {fileName}\n",
                fileName));
    }

    [Fact]
    public void UpdaterArguments_RejectDuplicateUnknownAndRelativeValues()
    {
        var valid = new[]
        {
            "--parent-pid", "123",
            "--parent-start-utc-ticks", "638900000000000000",
            "--msi", @"C:\updates\setup.msi",
            "--sha256", new string('A', 64),
            "--app", @"C:\Program Files\GitKeyRouter\GitKeyRouter.exe",
            "--state", @"C:\ProgramData\GitKeyRouter\last-update.json",
            "--log", @"C:\ProgramData\GitKeyRouter\update.log",
            "--version", "1.2.3"
        };

        var parsed = UpdateArguments.Parse(valid);
        Assert.Equal(123, parsed.ParentPid);

        Assert.Throws<ArgumentException>(() => UpdateArguments.Parse([.. valid, "--extra", "x"]));
        Assert.Throws<ArgumentException>(() =>
            UpdateArguments.Parse([
                "--parent-pid", "123",
                "--parent-pid", "456"
            ]));
        var relative = valid.ToArray();
        relative[5] = "setup.msi";
        Assert.Throws<ArgumentException>(() => UpdateArguments.Parse(relative));
    }

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.NotEmpty(_responses);
            var response = _responses.Dequeue();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
