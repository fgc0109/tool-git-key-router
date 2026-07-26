using System.Diagnostics;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Infrastructure.ProcessExecution;

namespace GitKeyRouter.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Run_BoundsBothStreamsAndRetainsHeadAndTail()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(new ProcessRequest
        {
            ExecutablePath = "cmd.exe",
            Arguments =
            [
                "/d",
                "/c",
                "for /L %i in (1,1,20) do @echo stdout-%i & for /L %i in (1,1,20) do @echo stderr-%i 1>&2"
            ],
            MaxOutputLines = 6
        });

        Assert.True(result.Succeeded);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
        Assert.Contains("stdout-1", result.StandardOutput);
        Assert.Contains("stdout-20", result.StandardOutput);
        Assert.DoesNotContain("stdout-10", result.StandardOutput);
        Assert.Contains("line(s) omitted", result.StandardOutput);
        Assert.Contains("stderr-1", result.StandardError);
        Assert.Contains("stderr-20", result.StandardError);
    }

    [Fact]
    public async Task Run_TruncatesOversizedIndividualLine()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(new ProcessRequest
        {
            ExecutablePath = "cmd.exe",
            Arguments = ["/d", "/c", "(for /L %i in (1,1,300) do @<nul set /p =x) & @echo."],
            MaxOutputCharactersPerLine = 80
        });

        Assert.True(result.Succeeded);
        Assert.True(result.StandardOutputTruncated);
        Assert.Contains("line truncated", result.StandardOutput);
        Assert.True(result.StandardOutput.Length <= 80);
    }

    [Fact]
    public async Task Run_ReportsStartFailureSeparately()
    {
        var runner = new ProcessRunner();
        var executable = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe");

        var result = await runner.RunAsync(new ProcessRequest
        {
            ExecutablePath = executable
        });

        Assert.False(result.Started);
        Assert.NotNull(result.StartException);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.KillFailed);
    }

    [Fact]
    public async Task Run_ReportsTimeoutAndTerminatesProcessTree()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(SlowRequest(TimeSpan.FromMilliseconds(100)));

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.KillFailed);
        Assert.Null(result.TerminationError);
    }

    [Fact]
    public async Task Run_ReportsUserCancellationSeparately()
    {
        var runner = new ProcessRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await runner.RunAsync(
            SlowRequest(TimeSpan.FromSeconds(10)),
            cancellation.Token);

        Assert.False(result.TimedOut);
        Assert.True(result.Cancelled);
        Assert.False(result.KillFailed);
    }

    [Fact]
    public async Task Run_ReportsTerminationFailureWithoutHidingTimeout()
    {
        var runner = new ReportingTerminationFailureRunner();

        var result = await runner.RunAsync(SlowRequest(TimeSpan.FromMilliseconds(100)));

        Assert.True(result.TimedOut);
        Assert.True(result.KillFailed);
        Assert.Contains("Simulated termination diagnostic", result.TerminationError);
        Assert.False(result.Succeeded);
    }

    private static ProcessRequest SlowRequest(TimeSpan timeout)
        => new()
        {
            ExecutablePath = "cmd.exe",
            Arguments = ["/d", "/c", "ping 127.0.0.1 -n 6 > nul"],
            Timeout = timeout,
            TerminationWaitTimeout = TimeSpan.FromSeconds(2)
        };

    private sealed class ReportingTerminationFailureRunner : ProcessRunner
    {
        protected override async Task<string?> TerminateProcessAsync(Process process, TimeSpan waitTimeout)
        {
            await base.TerminateProcessAsync(process, waitTimeout);
            return "Simulated termination diagnostic.";
        }
    }
}
