using System.Text.RegularExpressions;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Validation;

namespace GitKeyRouter.Core.Services;

public sealed partial class SshConfigService
{
    public const string BeginPrefix = "# BEGIN GitKeyRouter managed block: ";
    public const string EndPrefix = "# END GitKeyRouter managed block: ";

    private readonly IFileSystem _fileSystem;
    private readonly IAppPaths _paths;
    private readonly IBackupService _backupService;
    private readonly GitProviderAdapterRegistry _providers;

    public SshConfigService(
        IFileSystem fileSystem,
        IAppPaths paths,
        IBackupService backupService,
        GitProviderAdapterRegistry? providers = null)
    {
        _fileSystem = fileSystem;
        _paths = paths;
        _backupService = backupService;
        _providers = providers ?? GitProviderAdapterRegistry.CreateDefault();
    }

    public async Task<string> ReadRawAsync(CancellationToken cancellationToken = default)
    {
        if (!_fileSystem.FileExists(_paths.SshConfigPath))
        {
            return string.Empty;
        }

        return await _fileSystem.ReadAllTextAsync(_paths.SshConfigPath, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<SshManagedBlock> ParseManagedBlocks(string text)
    {
        var blocks = new List<SshManagedBlock>();
        foreach (Match match in ManagedBlockPattern().Matches(text))
        {
            blocks.Add(new SshManagedBlock
            {
                HostAlias = match.Groups["alias"].Value.Trim(),
                RawText = match.Value,
                StartIndex = match.Index,
                Length = match.Length
            });
        }

        return blocks;
    }

    public IReadOnlyList<string> ParseUnmanagedHostAliases(string text)
    {
        var unmanagedText = ManagedBlockPattern().Replace(text, string.Empty);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in unmanagedText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Host ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var token in trimmed[5..].Split(
                         [' ', '\t'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.StartsWith('!') || token.Contains('*') || token.Contains('?'))
                {
                    continue;
                }

                result.Add(token);
            }
        }

        return result.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public ChangePreview PreviewUpsert(string original, GitIdentity identity)
        => PreviewUpsert(original, GitServiceInstance.CreateGitHubCom(), identity);

    public ChangePreview PreviewUpsert(
        string original,
        GitServiceInstance service,
        GitIdentity identity)
    {
        var validation = HostAliasValidator.Validate(identity.HostAlias);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));
        }

        var blocks = ParseManagedBlocks(original)
            .Where(block => string.Equals(block.HostAlias, identity.HostAlias, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (blocks.Count > 1)
        {
            throw new InvalidOperationException($"Multiple managed blocks exist for HostAlias '{identity.HostAlias}'. Resolve duplicates before saving.");
        }

        var newline = DetectNewline(original);
        var blockText = _providers.Get(service.ProviderKind).BuildSshManagedBlock(service, identity, newline);
        string updated;
        if (blocks.Count == 1)
        {
            var block = blocks[0];
            updated = original.Remove(block.StartIndex, block.Length).Insert(block.StartIndex, blockText);
        }
        else
        {
            var separator = string.IsNullOrEmpty(original)
                ? string.Empty
                : original.EndsWith("\r\n", StringComparison.Ordinal) || original.EndsWith('\n') || original.EndsWith('\r')
                    ? newline
                    : newline + newline;
            updated = original + separator + blockText;
        }

        return CreatePreview(
            $"Update SSH managed block: {identity.HostAlias}",
            original,
            updated);
    }

    public ChangePreview PreviewDelete(string original, string hostAlias)
    {
        var blocks = ParseManagedBlocks(original)
            .Where(block => string.Equals(block.HostAlias, hostAlias, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(block => block.StartIndex)
            .ToList();

        var updated = original;
        foreach (var block in blocks)
        {
            updated = updated.Remove(block.StartIndex, block.Length);
        }

        updated = CollapseExcessBlankLines(updated, DetectNewline(original));
        return CreatePreview(
            $"Delete SSH managed block: {hostAlias}",
            original,
            updated);
    }

    public ChangePreview PreviewSynchronizeAll(string original, IEnumerable<GitIdentity> identities)
    {
        var updated = original;
        foreach (var identity in identities.OrderBy(item => item.HostAlias, StringComparer.OrdinalIgnoreCase))
        {
            updated = PreviewUpsert(updated, identity).UpdatedText;
        }

        return CreatePreview(
            "Synchronize all GitKeyRouter SSH managed blocks",
            original,
            updated);
    }

    public ChangePreview PreviewSynchronizeAll(string original, AppConfig config)
    {
        var updated = original;
        foreach (var identity in config.Identities.OrderBy(item => item.HostAlias, StringComparer.OrdinalIgnoreCase))
        {
            var service = config.FindService(identity.ServiceInstanceId)
                ?? throw new InvalidOperationException($"Git service '{identity.ServiceInstanceId}' was not found.");
            updated = PreviewUpsert(updated, service, identity).UpdatedText;
        }

        return CreatePreview(
            "Synchronize all GitKeyRouter SSH managed blocks",
            original,
            updated);
    }

    public IReadOnlyList<string> FindOrphanManagedBlockAliases(
        string original,
        IEnumerable<GitIdentity> identities)
    {
        var configuredAliases = identities
            .Select(identity => identity.HostAlias)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ParseManagedBlocks(original)
            .Where(block => !configuredAliases.Contains(block.HostAlias))
            .Select(block => block.HostAlias)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ChangePreview PreviewSynchronizeAllStrict(string original, AppConfig config)
    {
        var configuredAliases = config.Identities
            .Select(identity => identity.HostAlias)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updated = original;
        foreach (var block in ParseManagedBlocks(original)
                     .Where(block => !configuredAliases.Contains(block.HostAlias))
                     .OrderByDescending(block => block.StartIndex))
        {
            updated = updated.Remove(block.StartIndex, block.Length);
        }

        updated = CollapseExcessBlankLines(updated, DetectNewline(original));
        foreach (var identity in config.Identities.OrderBy(item => item.HostAlias, StringComparer.OrdinalIgnoreCase))
        {
            var service = config.FindService(identity.ServiceInstanceId)
                ?? throw new InvalidOperationException($"Git service '{identity.ServiceInstanceId}' was not found.");
            updated = PreviewUpsert(updated, service, identity).UpdatedText;
        }

        return CreatePreview(
            "Strictly synchronize GitKeyRouter SSH managed blocks",
            original,
            updated);
    }

    public ChangePreview CreatePreview(string description, string original, string updated)
    {
        var fileExists = _fileSystem.FileExists(_paths.SshConfigPath);
        return new ChangePreview
        {
            Description = description,
            OriginalFile = FileContentSnapshot.Create(fileExists, original),
            OriginalText = original,
            UpdatedText = updated,
            DiffText = TextDiffService.CreateSimpleDiff(original, updated, "ssh_config.before", "ssh_config.after")
        };
    }

    public async Task<OperationResult> ApplyAsync(ChangePreview preview, string reason, CancellationToken cancellationToken = default)
    {
        if (!preview.HasChanges)
        {
            return OperationResult.Ok("SSH config already matches the requested state.");
        }

        var fileExists = _fileSystem.FileExists(_paths.SshConfigPath);
        var currentText = fileExists
            ? await _fileSystem.ReadAllTextAsync(_paths.SshConfigPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        if (!preview.OriginalFile.Matches(fileExists, currentText))
        {
            return OperationResult.Fail(
                "文件在预览后已发生变化，请重新生成预览。",
                $"SSH config conflict: {_paths.SshConfigPath}");
        }

        await _backupService.CreateSnapshotAsync(reason, cancellationToken).ConfigureAwait(false);
        _fileSystem.CreateDirectory(_paths.SshDirectory);

        if (_fileSystem.FileExists(_paths.SshConfigPath))
        {
            _fileSystem.CopyFile(_paths.SshConfigPath, _paths.LegacySshConfigBackupPath, true);
        }

        await _fileSystem.WriteAllTextAtomicAsync(_paths.SshConfigPath, preview.UpdatedText, cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok("SSH config was updated and backed up.");
    }

    public static string BuildManagedBlock(GitIdentity identity, string newline)
        => BuildManagedBlock(GitServiceInstance.CreateGitHubCom(), identity, newline);

    public static string BuildManagedBlock(
        GitServiceInstance service,
        GitIdentity identity,
        string newline)
        => GitProviderAdapterRegistry.CreateDefault()
            .Get(service.ProviderKind)
            .BuildSshManagedBlock(service, identity, newline);

    public static string ConvertWindowsPathToOpenSsh(string path)
        => Path.GetFullPath(path).Replace('\\', '/');

    public static string DetectNewline(string text)
    {
        if (text.Contains("\r\n", StringComparison.Ordinal))
        {
            return "\r\n";
        }

        return text.Contains('\n') ? "\n" : Environment.NewLine;
    }

    private static string CollapseExcessBlankLines(string text, string newline)
    {
        var triple = newline + newline + newline;
        while (text.Contains(triple, StringComparison.Ordinal))
        {
            text = text.Replace(triple, newline + newline, StringComparison.Ordinal);
        }

        return text;
    }

    [GeneratedRegex("(?ms)^# BEGIN GitKeyRouter managed block: (?<alias>[^\\r\\n]+)\\r?\\n.*?^# END GitKeyRouter managed block: \\k<alias>(?:\\r?\\n|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ManagedBlockPattern();
}
