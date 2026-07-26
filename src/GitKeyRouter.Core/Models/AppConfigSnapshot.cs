namespace GitKeyRouter.Core.Models;

public sealed record AppConfigFileVersion(bool Exists, string Sha256)
{
    public static AppConfigFileVersion Missing { get; } = new(false, string.Empty);
}

public sealed record AppConfigSnapshot(AppConfig Config, AppConfigFileVersion Version);

public sealed class AppConfigConcurrencyException : IOException
{
    public AppConfigConcurrencyException(string configPath)
        : base($"Application configuration changed after it was loaded: {configPath}. Reload and retry the operation.")
    {
    }
}
