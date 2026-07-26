using GitKeyRouter.Infrastructure.Logging;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class SafeFileLoggerTests
{
    [Fact]
    public void Write_RedactsPrivateKeyMaterial()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var logger = new SafeFileLogger(paths, maxFileBytes: 4096, retainedFileCount: 2);
        const string privateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nsecret-data\n-----END OPENSSH PRIVATE KEY-----";

        logger.Error(privateKey, new InvalidOperationException(privateKey));

        var content = File.ReadAllText(Path.Combine(paths.AppDataDirectory, "gitkeyrouter.log"));
        Assert.DoesNotContain("secret-data", content, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", content, StringComparison.Ordinal);
        Assert.Contains("[REDACTED OPENSSH PRIVATE KEY]", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RedactsCredentialsInMessagesAndExceptions()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var logger = new SafeFileLogger(paths, maxFileBytes: 4096, retainedFileCount: 2);

        logger.Error(
            "remote=https://alice:hunter2@example.com/repo.git",
            new InvalidOperationException("Authorization: Bearer exception-token"));

        var content = File.ReadAllText(Path.Combine(paths.AppDataDirectory, "gitkeyrouter.log"));
        Assert.DoesNotContain("hunter2", content, StringComparison.Ordinal);
        Assert.DoesNotContain("exception-token", content, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RotatesAndHonorsRetentionLimit()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        var logger = new SafeFileLogger(paths, maxFileBytes: 160, retainedFileCount: 2);

        for (var index = 1; index <= 4; index++)
        {
            logger.Information($"entry-{index}-{new string('x', 96)}");
        }

        var logPath = Path.Combine(paths.AppDataDirectory, "gitkeyrouter.log");
        Assert.Contains("entry-4-", File.ReadAllText(logPath), StringComparison.Ordinal);
        Assert.Contains("entry-3-", File.ReadAllText($"{logPath}.1"), StringComparison.Ordinal);
        Assert.Contains("entry-2-", File.ReadAllText($"{logPath}.2"), StringComparison.Ordinal);
        Assert.False(File.Exists($"{logPath}.3"));
        Assert.DoesNotContain(
            Directory.GetFiles(paths.AppDataDirectory, "gitkeyrouter.log*")
                .Select(File.ReadAllText),
            content => content.Contains("entry-1-", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_DoesNotThrowWhenLogTargetCannotBeOpened()
    {
        using var temp = new TemporaryDirectory();
        var paths = new TestAppPaths(temp.Path);
        Directory.CreateDirectory(Path.Combine(paths.AppDataDirectory, "gitkeyrouter.log"));
        var logger = new SafeFileLogger(paths);

        var exception = Record.Exception(() => logger.Warning("logging is best effort"));

        Assert.Null(exception);
    }
}
