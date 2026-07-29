using GitKeyRouter.Infrastructure.GitHub;
using System.Text.Json;

namespace GitKeyRouter.App.Cli;

public sealed partial class CliApplication
{
    private async Task<int> GitHubLoginAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine(
                "Usage: GitKeyRouter.exe gh-login <identity-id-or-host-alias>");
            return 3;
        }

        var result = await _services.GitHubCliService.LoginAsync(
            args[0],
            cancellationToken).ConfigureAwait(false);
        return PrintGitHubCliResult(result, printSuccess: true);
    }

    private async Task<int> GitHubStatusAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length > 1)
        {
            Console.Error.WriteLine(
                "Usage: GitKeyRouter.exe gh-status [identity-id-or-host-alias]");
            return 3;
        }

        var result = await _services.GitHubCliService.StatusAsync(
            args.FirstOrDefault(),
            Environment.CurrentDirectory,
            cancellationToken).ConfigureAwait(false);
        return PrintGitHubCliResult(result, printSuccess: true);
    }

    private async Task<int> GitHubResolveAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        string? identity = null;
        string? repository = null;
        var json = false;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase))
            {
                json = true;
                continue;
            }

            if (string.Equals(argument, "--identity", StringComparison.OrdinalIgnoreCase))
            {
                if (identity is not null || index + 1 >= args.Length)
                {
                    return PrintGitHubResolveUsage();
                }

                identity = args[++index];
                continue;
            }

            if (string.Equals(argument, "-R", StringComparison.OrdinalIgnoreCase)
                || string.Equals(argument, "--repo", StringComparison.OrdinalIgnoreCase))
            {
                if (repository is not null || index + 1 >= args.Length)
                {
                    return PrintGitHubResolveUsage();
                }

                repository = args[++index];
                continue;
            }

            if (argument.StartsWith("--repo=", StringComparison.OrdinalIgnoreCase))
            {
                if (repository is not null)
                {
                    return PrintGitHubResolveUsage();
                }

                repository = argument["--repo=".Length..];
                continue;
            }

            if (argument.StartsWith("-R", StringComparison.OrdinalIgnoreCase)
                && argument.Length > 2)
            {
                if (repository is not null)
                {
                    return PrintGitHubResolveUsage();
                }

                repository = argument[2..];
                continue;
            }

            return PrintGitHubResolveUsage();
        }

        var result = await _services.GitHubCliService.ResolveAsync(
            identity,
            repository,
            Environment.CurrentDirectory,
            cancellationToken).ConfigureAwait(false);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                result,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            var writer = result.Success ? Console.Out : Console.Error;
            writer.WriteLine(result.Message);
            writer.WriteLine($"gh: {result.GitHubCliPath ?? "<not found>"}");
            writer.WriteLine($"gh source: {result.GitHubCliSource ?? "<unknown>"}");
            writer.WriteLine($"gh version: {result.GitHubCliVersion ?? "<unknown>"}");
            writer.WriteLine($"repository root: {result.RepositoryRoot ?? "<none>"}");
            writer.WriteLine($"remote: {result.RemoteName ?? "<explicit>"}");
            writer.WriteLine($"remote source: {result.RemoteSelectionSource ?? "<explicit>"}");
            writer.WriteLine($"HostAlias: {result.HostAlias ?? "<none>"}");
            writer.WriteLine($"identity: {result.IdentityId ?? "<none>"}");
            writer.WriteLine($"account: {result.AccountName ?? "<none>"}");
            writer.WriteLine($"GH_HOST: {result.GitHubHost ?? "<none>"}");
            writer.WriteLine($"GH_REPO: {result.GitHubRepository ?? "<none>"}");
            foreach (var warning in result.Warnings ?? [])
            {
                writer.WriteLine($"warning: {warning}");
            }
        }

        return result.ExitCode;
    }

    private static int PrintGitHubResolveUsage()
    {
        Console.Error.WriteLine(
            "Usage: GitKeyRouter.exe gh-resolve [--identity <identity-id-or-host-alias>] [-R|--repo <host/owner/repo>] [--json]");
        return 3;
    }

    private async Task<int> GitHubAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        var separator = Array.IndexOf(args, "--");
        if (separator < 0)
        {
            Console.Error.WriteLine(
                "Usage: GitKeyRouter.exe gh [--identity <identity-id-or-host-alias>] -- <gh-arguments>");
            return 3;
        }

        string? identity = null;
        for (var index = 0; index < separator; index++)
        {
            if (!string.Equals(args[index], "--identity", StringComparison.OrdinalIgnoreCase)
                || index + 1 >= separator
                || identity is not null)
            {
                Console.Error.WriteLine(
                    "Only one --identity <identity-id-or-host-alias> option is allowed before --.");
                return 3;
            }

            identity = args[++index];
        }

        var forwardedArguments = args[(separator + 1)..];
        if (forwardedArguments.Length == 0)
        {
            Console.Error.WriteLine("At least one GitHub CLI argument is required after --.");
            return 3;
        }

        var result = await _services.GitHubCliService.RunAsync(
            forwardedArguments,
            identity,
            Environment.CurrentDirectory,
            cancellationToken).ConfigureAwait(false);
        return PrintGitHubCliResult(result, printSuccess: false);
    }

    private static int PrintGitHubCliResult(
        GitHubCliCommandResult result,
        bool printSuccess)
    {
        if (result.Success)
        {
            if (printSuccess && !string.IsNullOrWhiteSpace(result.Message))
            {
                Console.WriteLine(result.Message);
            }
        }
        else if (!string.IsNullOrWhiteSpace(result.Message))
        {
            Console.Error.WriteLine(result.Message);
        }

        return result.ExitCode;
    }
}
