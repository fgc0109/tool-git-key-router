using System.Text;
using System.Text.RegularExpressions;

namespace GitKeyRouter.Infrastructure.Logging;

public static partial class SensitiveDataRedactor
{
    private const string RedactedPrivateKey = "[REDACTED OPENSSH PRIVATE KEY]";
    private const string RedactedCredential = "[REDACTED]";

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var redacted = PrivateKeyPattern().Replace(value, RedactedPrivateKey);
        var builder = new StringBuilder(redacted.Length);
        var start = 0;
        while (start < redacted.Length)
        {
            var newline = redacted.IndexOf('\n', start);
            var end = newline < 0 ? redacted.Length : newline;
            builder.Append(RedactCredentialLine(redacted[start..end]));
            if (newline < 0)
            {
                break;
            }

            builder.Append('\n');
            start = newline + 1;
        }

        return builder.ToString();
    }

    private static string RedactCredentialLine(string value)
    {
        var redacted = BearerPattern().Replace(value, "${prefix}[REDACTED]");
        redacted = CredentialUrlPattern().Replace(redacted, "${scheme}://[REDACTED]@");
        redacted = SecretAssignmentPattern().Replace(redacted, "${prefix}[REDACTED]");
        return PlatformTokenPattern().Replace(redacted, RedactedCredential);
    }

    [GeneratedRegex(
        "-----BEGIN (?:OPENSSH|RSA|EC|DSA) PRIVATE KEY-----.*?-----END (?:OPENSSH|RSA|EC|DSA) PRIVATE KEY-----",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        250)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex(
        "(?<scheme>https?|ftp)://[^\\s/]+@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        250)]
    private static partial Regex CredentialUrlPattern();

    [GeneratedRegex(
        "(?<prefix>\\bAuthorization\\s*(?::|=)\\s*Bearer\\s+)[^\\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        250)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(
        "(?<prefix>\\b(?:password|passwd|pwd|token|access[_-]?token|api[_-]?key|secret|client[_-]?secret|GIT_ASKPASS|SSH_ASKPASS)\\b\\s*(?::|=)\\s*)(?:\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s,;}\\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        250)]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(
        "\\b(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|glpat-[A-Za-z0-9_-]{20,})\\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        250)]
    private static partial Regex PlatformTokenPattern();
}
