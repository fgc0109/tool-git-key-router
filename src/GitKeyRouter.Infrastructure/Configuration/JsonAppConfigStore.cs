using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Core.Services;

namespace GitKeyRouter.Infrastructure.Configuration;

public sealed class JsonAppConfigStore : IAppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IFileSystem _fileSystem;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonAppConfigStore(IAppPaths paths, IFileSystem fileSystem)
    {
        ConfigPath = paths.ConfigPath;
        _fileSystem = fileSystem;
    }

    public string ConfigPath { get; }

    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
        => (await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false)).Config;

    public async Task<AppConfigSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_fileSystem.FileExists(ConfigPath))
            {
                return new AppConfigSnapshot(new AppConfig(), AppConfigFileVersion.Missing);
            }

            var bytes = await _fileSystem.ReadAllBytesAsync(ConfigPath, cancellationToken).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text[1..];
            }

            return new AppConfigSnapshot(
                Parse(text),
                new AppConfigFileVersion(true, ComputeSha256(bytes)));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            config.Normalize();
            var text = JsonSerializer.Serialize(config, JsonOptions) + Environment.NewLine;
            await _fileSystem.WriteAllTextAtomicAsync(ConfigPath, text, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppConfigFileVersion> SaveIfUnchangedAsync(
        AppConfig config,
        AppConfigFileVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(expectedVersion);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var actualVersion = await ReadVersionAsync(cancellationToken).ConfigureAwait(false);
            if (actualVersion != expectedVersion)
            {
                throw new AppConfigConcurrencyException(ConfigPath);
            }

            config.Normalize();
            var text = JsonSerializer.Serialize(config, JsonOptions) + Environment.NewLine;
            await _fileSystem.WriteAllTextAtomicAsync(ConfigPath, text, cancellationToken).ConfigureAwait(false);
            return new AppConfigFileVersion(true, ComputeSha256(Encoding.UTF8.GetBytes(text)));
        }
        finally
        {
            _gate.Release();
        }
    }

    private AppConfig Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var schemaVersion = AppConfigSchemaReader.Read(document.RootElement);
            if (schemaVersion < 1)
            {
                throw new InvalidDataException($"Configuration schema {schemaVersion} is invalid.");
            }

            if (schemaVersion > AppConfig.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Configuration schema {schemaVersion} is newer than the supported schema {AppConfig.CurrentSchemaVersion}.");
            }

            var config = schemaVersion <= 1
                ? MigrateSchema1(text)
                : JsonSerializer.Deserialize<AppConfig>(text, JsonOptions)
                    ?? throw new InvalidDataException("The application configuration is empty.");
            config.Normalize();
            return config;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"GitKeyRouter could not parse '{ConfigPath}'. The file was not modified. Restore a backup or correct the JSON.",
                exception);
        }
    }

    private async Task<AppConfigFileVersion> ReadVersionAsync(CancellationToken cancellationToken)
    {
        if (!_fileSystem.FileExists(ConfigPath))
        {
            return AppConfigFileVersion.Missing;
        }

        var bytes = await _fileSystem.ReadAllBytesAsync(ConfigPath, cancellationToken).ConfigureAwait(false);
        return new AppConfigFileVersion(true, ComputeSha256(bytes));
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private static AppConfig MigrateSchema1(string text)
    {
        var legacy = JsonSerializer.Deserialize<Schema1Config>(text, JsonOptions)
            ?? throw new InvalidDataException("The Schema 1 application configuration is empty.");
        return AppConfigMigrator.FromSchema1(legacy.Identities ?? [], legacy.OwnerRoutes ?? []);
    }

    private sealed class Schema1Config
    {
        public int SchemaVersion { get; set; } = 1;

        public List<AppConfigMigrator.Schema1GitHubIdentity>? Identities { get; set; }

        public List<AppConfigMigrator.Schema1OwnerRoute>? OwnerRoutes { get; set; }
    }
}
