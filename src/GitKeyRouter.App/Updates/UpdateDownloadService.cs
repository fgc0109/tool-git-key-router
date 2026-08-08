using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace GitKeyRouter.App.Updates;

public sealed record VerifiedUpdatePackage(
    string FilePath,
    string FileName,
    string Sha256,
    Version Version,
    UpdatePackageKind PackageKind);

public sealed class UpdateDownloadService
{
    private const long MaxChecksumBytes = 1024 * 1024;
    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly Regex ChecksumLinePattern = new(
        @"^(?<hash>[A-Fa-f0-9]{64})\s+\*?(?<name>[^\r\n]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _rootDirectory;
    private readonly HttpClient _httpClient;

    public UpdateDownloadService(string rootDirectory, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Update download directory is required.", nameof(rootDirectory));
        }

        _rootDirectory = Path.GetFullPath(rootDirectory);
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<VerifiedUpdatePackage> DownloadVerifiedInstallerAsync(
        UpdateReleaseInfo release,
        UpdatePackageKind packageKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!release.HasVerifiedInstallerDownload(packageKind))
        {
            throw new InvalidOperationException("The selected release does not contain a verifiable installer.");
        }

        var packageUri = release.PreferredDownload(packageKind)!;
        var checksumUri = release.ChecksumsUrl!;
        var fileName = release.PreferredFileName(packageKind);
        GitHubUpdateChecker.ValidateCanonicalAssetUrl(release.TagName, fileName, packageUri);
        GitHubUpdateChecker.ValidateCanonicalAssetUrl(release.TagName, "SHA256SUMS.txt", checksumUri);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        var checksumText = await DownloadTextBoundedAsync(checksumUri, MaxChecksumBytes, timeout.Token).ConfigureAwait(false);
        var expectedSha256 = ParseExpectedSha256(checksumText, fileName);

        var versionDirectory = Path.Combine(_rootDirectory, $"v{release.Version}");
        Directory.CreateDirectory(versionDirectory);
        var destinationPath = Path.Combine(versionDirectory, fileName);
        if (File.Exists(destinationPath)
            && string.Equals(await ComputeSha256Async(destinationPath, timeout.Token).ConfigureAwait(false),
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return new VerifiedUpdatePackage(destinationPath, fileName, expectedSha256, release.Version, packageKind);
        }

        var partialPath = destinationPath + ".partial";
        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }

        try
        {
            using var response = await _httpClient.GetAsync(
                packageUri,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxPackageBytes)
            {
                throw new InvalidDataException("The update installer exceeds the maximum allowed size.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long total = 0;
            try
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > MaxPackageBytes)
                    {
                        throw new InvalidDataException("The update installer exceeds the maximum allowed size.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);

            var actualHash = Convert.ToHexString(hash.GetHashAndReset());
            if (!FixedTimeHexEquals(actualHash, expectedSha256))
            {
                throw new InvalidDataException("The downloaded update installer failed SHA-256 verification.");
            }

            File.Move(partialPath, destinationPath, overwrite: true);
            return new VerifiedUpdatePackage(destinationPath, fileName, expectedSha256, release.Version, packageKind);
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
    }

    public static string ParseExpectedSha256(string checksumText, string fileName)
    {
        if (string.IsNullOrWhiteSpace(checksumText))
        {
            throw new InvalidDataException("The release checksum file is empty.");
        }

        var matches = checksumText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => ChecksumLinePattern.Match(line))
            .Where(match => match.Success && string.Equals(match.Groups["name"].Value.Trim(), fileName, StringComparison.Ordinal))
            .Select(match => match.Groups["hash"].Value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException($"SHA256SUMS.txt does not contain '{fileName}'."),
            _ => throw new InvalidDataException($"SHA256SUMS.txt contains conflicting entries for '{fileName}'.")
        };
    }

    private async Task<string> DownloadTextBoundedAsync(Uri uri, long maxBytes, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
        {
            throw new InvalidDataException("The release checksum file is too large.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > maxBytes)
            {
                throw new InvalidDataException("The release checksum file is too large.");
            }

            memory.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static bool FixedTimeHexEquals(string actual, string expected)
    {
        if (actual.Length != expected.Length)
        {
            return false;
        }

        try
        {
            var actualBytes = Convert.FromHexString(actual);
            var expectedBytes = Convert.FromHexString(expected);
            return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
