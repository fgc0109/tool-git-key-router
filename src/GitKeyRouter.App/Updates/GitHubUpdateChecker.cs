using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitKeyRouter.App.Updates;

public enum UpdateReleaseSource
{
    PagesManifest,
    GitHubApi
}

public sealed record UpdateReleaseInfo(
    string TagName,
    Version Version,
    Uri ReleasePage,
    Uri? PortableFrameworkDependent,
    Uri? PortableSelfContained,
    Uri? InstallerFrameworkDependent,
    Uri? InstallerSelfContained,
    Uri? ChecksumsUrl,
    string Notes,
    UpdateReleaseSource Source)
{
    public Uri? PreferredDownload(UpdatePackageKind kind) => kind switch
    {
        UpdatePackageKind.PortableFrameworkDependent => PortableFrameworkDependent,
        UpdatePackageKind.PortableSelfContained => PortableSelfContained,
        UpdatePackageKind.InstallerFrameworkDependent => InstallerFrameworkDependent,
        UpdatePackageKind.InstallerSelfContained => InstallerSelfContained,
        _ => null
    };

    public string PreferredFileName(UpdatePackageKind kind)
    {
        var releaseVersion = TagName.StartsWith('v') || TagName.StartsWith('V') ? TagName[1..] : TagName;
        return kind switch
        {
            UpdatePackageKind.PortableFrameworkDependent =>
                $"GitKeyRouter-v{releaseVersion}-win-x64-framework-dependent.zip",
            UpdatePackageKind.PortableSelfContained =>
                $"GitKeyRouter-v{releaseVersion}-win-x64-portable.zip",
            UpdatePackageKind.InstallerFrameworkDependent =>
                $"GitKeyRouter-v{releaseVersion}-win-x64-framework-dependent-setup.msi",
            UpdatePackageKind.InstallerSelfContained =>
                $"GitKeyRouter-v{releaseVersion}-win-x64-setup.msi",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public bool HasVerifiedInstallerDownload(UpdatePackageKind kind)
        => kind is UpdatePackageKind.InstallerFrameworkDependent or UpdatePackageKind.InstallerSelfContained
           && PreferredDownload(kind) is not null
           && ChecksumsUrl is not null;
}

public sealed class GitHubUpdateChecker
{
    private const int MaxNotesLength = 20_000;
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(8);
    private static readonly Regex StableVersionPattern = new(
        @"^v?(?<version>\d+\.\d+\.\d+(?:\.\d+)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly HttpClient _httpClient;

    public GitHubUpdateChecker(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateDefaultHttpClient();
    }

    public async Task<UpdateReleaseInfo> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OverallTimeout);

        Exception? manifestFailure = null;
        try
        {
            using var response = await _httpClient.GetAsync(UpdateProjectLinks.Manifest, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ParseManifest(json);
        }
        catch (Exception exception) when (IsExpectedNetworkOrPayloadFailure(exception, cancellationToken))
        {
            manifestFailure = exception;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UpdateProjectLinks.LatestReleaseApi);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ParseGitHubRelease(json);
        }
        catch (Exception exception) when (IsExpectedNetworkOrPayloadFailure(exception, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Unable to check for GitKeyRouter updates. Pages manifest failed: {FriendlyMessage(manifestFailure)}; " +
                $"GitHub release API failed: {FriendlyMessage(exception)}",
                exception);
        }
    }

    public static bool IsNewer(Version current, UpdateReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(release);
        return release.Version > NormalizeVersion(current);
    }

    public static Version? TryParseStableVersion(string? tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        var match = StableVersionPattern.Match(tagName.Trim());
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version)
            ? NormalizeVersion(version)
            : null;
    }

    public static UpdateReleaseInfo ParseManifest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var schemaVersion = root.TryGetProperty("schemaVersion", out var schemaElement)
            ? schemaElement.GetInt32()
            : 1;
        if (schemaVersion is < 1 or > 3)
        {
            throw new InvalidDataException($"Unsupported update manifest schema {schemaVersion}.");
        }

        var tagName = RequireString(root, "tagName");
        var version = TryParseStableVersion(tagName)
            ?? throw new InvalidDataException("The update manifest tag is not a stable numeric version.");
        if (root.TryGetProperty("version", out var versionElement)
            && versionElement.ValueKind == JsonValueKind.String)
        {
            var explicitVersion = TryParseStableVersion(versionElement.GetString());
            if (explicitVersion is null || explicitVersion != version)
            {
                throw new InvalidDataException("The update manifest tag and version disagree.");
            }
        }

        var releasePage = RequireHttpsUri(RequireString(root, "releasePage"), "releasePage");
        Uri? portableFramework = null;
        Uri? portableSelfContained = null;
        Uri? installerFramework = null;
        Uri? installerSelfContained = null;

        if (schemaVersion >= 3 && root.TryGetProperty("downloads", out var downloads))
        {
            portableFramework = OptionalHttpsUri(downloads, "portableFrameworkDependent");
            portableSelfContained = OptionalHttpsUri(downloads, "portableSelfContained");
            installerFramework = OptionalHttpsUri(downloads, "installerFrameworkDependent");
            installerSelfContained = OptionalHttpsUri(downloads, "installerSelfContained");
        }
        else if (root.TryGetProperty("downloadUrl", out var legacyDownload)
                 && legacyDownload.ValueKind == JsonValueKind.String)
        {
            portableSelfContained = RequireHttpsUri(legacyDownload.GetString()!, "downloadUrl");
        }

        var checksumsUrl = OptionalHttpsUri(root, "checksumsUrl");
        var notes = root.TryGetProperty("notes", out var notesElement) && notesElement.ValueKind == JsonValueKind.String
            ? BoundNotes(notesElement.GetString())
            : string.Empty;

        ValidateCanonicalReleaseAssets(
            tagName,
            portableFramework,
            portableSelfContained,
            installerFramework,
            installerSelfContained,
            checksumsUrl);

        return new UpdateReleaseInfo(
            tagName,
            version,
            releasePage,
            portableFramework,
            portableSelfContained,
            installerFramework,
            installerSelfContained,
            checksumsUrl,
            notes,
            UpdateReleaseSource.PagesManifest);
    }

    public static UpdateReleaseInfo ParseGitHubRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
        {
            throw new InvalidDataException("The latest GitHub release is a draft.");
        }

        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True)
        {
            throw new InvalidDataException("The latest GitHub release is a prerelease.");
        }

        var tagName = RequireString(root, "tag_name");
        var version = TryParseStableVersion(tagName)
            ?? throw new InvalidDataException("The latest GitHub release tag is not a stable numeric version.");
        var releasePage = RequireHttpsUri(RequireString(root, "html_url"), "html_url");

        var expected = ExpectedAssetNames(tagName);
        var assets = new Dictionary<string, Uri>(StringComparer.Ordinal);
        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameElement)
                    || nameElement.ValueKind != JsonValueKind.String
                    || !asset.TryGetProperty("browser_download_url", out var urlElement)
                    || urlElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name) || !expected.Values.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                var url = RequireHttpsUri(urlElement.GetString()!, $"asset {name}");
                ValidateCanonicalAssetUrl(tagName, name, url);
                assets[name] = url;
            }
        }

        Uri? Get(string key) => assets.TryGetValue(expected[key], out var value) ? value : null;
        var notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String
            ? BoundNotes(body.GetString())
            : string.Empty;

        return new UpdateReleaseInfo(
            tagName,
            version,
            releasePage,
            Get("portableFramework"),
            Get("portableSelfContained"),
            Get("installerFramework"),
            Get("installerSelfContained"),
            Get("checksums"),
            notes,
            UpdateReleaseSource.GitHubApi);
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GitKeyRouter-UpdateChecker/1.0");
        return client;
    }

    private static string RequireString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"The update payload is missing '{propertyName}'.");
        }

        return value.GetString()!;
    }

    private static Uri? OptionalHttpsUri(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"The update payload contains an invalid '{propertyName}'.");
        }

        return RequireHttpsUri(value.GetString()!, propertyName);
    }

    private static Uri RequireHttpsUri(string raw, string field)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException($"The update payload field '{field}' must be an absolute HTTPS URL.");
        }

        return uri;
    }

    private static void ValidateCanonicalReleaseAssets(
        string tagName,
        Uri? portableFramework,
        Uri? portableSelfContained,
        Uri? installerFramework,
        Uri? installerSelfContained,
        Uri? checksums)
    {
        var version = TryParseStableVersion(tagName)!;
        var expected = ExpectedAssetNames(tagName);
        ValidateOptional("portableFramework", portableFramework);
        ValidateOptional("portableSelfContained", portableSelfContained);
        ValidateOptional("installerFramework", installerFramework);
        ValidateOptional("installerSelfContained", installerSelfContained);
        ValidateOptional("checksums", checksums);

        void ValidateOptional(string key, Uri? uri)
        {
            if (uri is not null)
            {
                ValidateCanonicalAssetUrl(tagName, expected[key], uri);
            }
        }
    }

    internal static void ValidateCanonicalAssetUrl(string tagName, string fileName, Uri uri)
    {
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Update asset '{fileName}' must be hosted on github.com.");
        }

        var expectedPath =
            $"/{UpdateProjectLinks.GitHubOwner}/{UpdateProjectLinks.GitHubRepository}/releases/download/{tagName}/{fileName}";
        if (!string.Equals(Uri.UnescapeDataString(uri.AbsolutePath), expectedPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Update asset '{fileName}' does not use the canonical release path.");
        }
    }

    private static Dictionary<string, string> ExpectedAssetNames(string tagName)
    {
        var releaseVersion = tagName.StartsWith('v') || tagName.StartsWith('V') ? tagName[1..] : tagName;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["portableFramework"] = $"GitKeyRouter-v{releaseVersion}-win-x64-framework-dependent.zip",
            ["portableSelfContained"] = $"GitKeyRouter-v{releaseVersion}-win-x64-portable.zip",
            ["installerFramework"] = $"GitKeyRouter-v{releaseVersion}-win-x64-framework-dependent-setup.msi",
            ["installerSelfContained"] = $"GitKeyRouter-v{releaseVersion}-win-x64-setup.msi",
            ["checksums"] = "SHA256SUMS.txt"
        };
    }

    private static string BoundNotes(string? notes)
    {
        var value = notes ?? string.Empty;
        return value.Length <= MaxNotesLength ? value : value[..MaxNotesLength];
    }

    private static Version NormalizeVersion(Version version)
        => new(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));

    private static bool IsExpectedNetworkOrPayloadFailure(Exception exception, CancellationToken callerToken)
        => exception is HttpRequestException or InvalidDataException or JsonException
            || (exception is TaskCanceledException && !callerToken.IsCancellationRequested);

    private static string FriendlyMessage(Exception? exception)
        => exception switch
        {
            null => "unknown error",
            TaskCanceledException => "request timed out",
            HttpRequestException http when http.StatusCode is HttpStatusCode status =>
                $"HTTP {(int)status}",
            _ => exception.Message
        };
}
