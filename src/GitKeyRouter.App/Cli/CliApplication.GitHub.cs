using GitKeyRouter.Infrastructure.GitHub;

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
