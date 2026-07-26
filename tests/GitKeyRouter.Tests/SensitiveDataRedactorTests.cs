using GitKeyRouter.Infrastructure.Logging;

namespace GitKeyRouter.Tests;

public sealed class SensitiveDataRedactorTests
{
    [Fact]
    public void Redact_RemovesPrivateKeyBody()
    {
        var input = "before\n-----BEGIN OPENSSH PRIVATE KEY-----\nsecret-data\n-----END OPENSSH PRIVATE KEY-----\nafter";

        var output = SensitiveDataRedactor.Redact(input);

        Assert.DoesNotContain("secret-data", output);
        Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", output);
        Assert.Contains("[REDACTED OPENSSH PRIVATE KEY]", output);
    }

    [Theory]
    [InlineData("clone https://alice:hunter2@example.com/team/repo.git", "hunter2")]
    [InlineData("clone https://token-value@example.com/team/repo.git", "token-value")]
    [InlineData("clone https://alice:p@ss@example.com/team/repo.git", "p@ss")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1Ni.secret", "eyJhbGciOiJIUzI1Ni.secret")]
    [InlineData("Authorization=Bearer opaque-value", "opaque-value")]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("api_key: \"key with spaces\"", "key with spaces")]
    [InlineData("client-secret='client value'", "client value")]
    [InlineData("GIT_ASKPASS=C:\\secret-tools\\askpass.exe", "C:\\secret-tools\\askpass.exe")]
    [InlineData("SSH_ASKPASS=/opt/private/askpass", "/opt/private/askpass")]
    [InlineData("token=ghp_abcdefghijklmnopqrstuvwxyz1234567890", "ghp_abcdefghijklmnopqrstuvwxyz1234567890")]
    [InlineData("github_pat_11AA22BB33CC44DD55EE66FF77GG", "github_pat_11AA22BB33CC44DD55EE66FF77GG")]
    [InlineData("glpat-abcdefghijklmnopqrstuvwxyz12", "glpat-abcdefghijklmnopqrstuvwxyz12")]
    public void Redact_RemovesCommonCredentialFormats(string input, string secret)
    {
        var output = SensitiveDataRedactor.Redact(input);

        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git")]
    [InlineData("git@github.com:org/repo.git")]
    [InlineData("ssh://git@git.example.com:2222/org/repo.git")]
    [InlineData("C:\\secret\\project\\config.json")]
    [InlineData("/home/user/token/cache.txt")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIEexamplekeymaterial user@example")]
    [InlineData("SHA256:AbCdEf0123456789+/fingerprint")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("token count = 42")]
    public void Redact_PreservesNonCredentialValues(string input)
        => Assert.Equal(input, SensitiveDataRedactor.Redact(input));

    [Fact]
    public void Redact_ProcessesLongInputWithinBoundedTime()
    {
        var input = string.Concat(Enumerable.Repeat(
            "normal https://example.com/path C:\\work\\repo 0123456789abcdef0123456789abcdef01234567\n",
            20_000)) + "Authorization: Bearer final-secret";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var output = SensitiveDataRedactor.Redact(input);

        stopwatch.Stop();
        Assert.DoesNotContain("final-secret", output, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Redaction took {stopwatch.Elapsed}.");
    }
}
