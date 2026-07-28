using System.Diagnostics;
using GitKeyRouter.Core.Models;
using GitKeyRouter.Infrastructure.ProcessExecution;
using GitKeyRouter.Tests.TestSupport;

namespace GitKeyRouter.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Run_BoundsBothStreamsAndRetainsHeadAndTail()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync(new ProcessRequest
        {
            ExecutablePath = ChildExecutablePath,
            Arguments = ["emit-lines", "20"],
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
            ExecutablePath = ChildExecutablePath,
            Arguments = ["emit-line", "300"],
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
    public async Task Run_ReportsTimeoutAfterChildSignalsReady()
    {
        using var temp = new TemporaryDirectory();
        var readyPath = Path.Combine(temp.Path, "timeout.ready");
        var runner = new ProcessRunner();
        var runTask = runner.RunAsync(WaitRequest(readyPath, TimeSpan.FromSeconds(2)));
        await WaitForReadyAsync(readyPath, runTask);

        var result = await runTask;

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.KillFailed);
        Assert.Null(result.TerminationError);
    }

    [Fact]
    public async Task Run_ReportsUserCancellationAfterChildSignalsReady()
    {
        using var temp = new TemporaryDirectory();
        var readyPath = Path.Combine(temp.Path, "cancel.ready");
        var runner = new ProcessRunner();
        using var cancellation = new CancellationTokenSource();
        var runTask = runner.RunAsync(
            WaitRequest(readyPath, TimeSpan.FromSeconds(30)),
            cancellation.Token);
        await WaitForReadyAsync(readyPath, runTask);

        cancellation.Cancel();
        var result = await runTask;

        Assert.False(result.TimedOut);
        Assert.True(result.Cancelled);
        Assert.False(result.KillFailed);
    }

    [Fact]
    public async Task Run_CancellationTerminatesSpawnedChildTree()
    {
        using var temp = new TemporaryDirectory();
        var parentReadyPath = Path.Combine(temp.Path, "parent.ready");
        var childReadyPath = Path.Combine(temp.Path, "child.ready");
        var runner = new ProcessRunner();
        using var cancellation = new CancellationTokenSource();
        var request = new ProcessRequest
        {
            ExecutablePath = ChildExecutablePath,
            Arguments = ["spawn-tree", parentReadyPath, childReadyPath],
            Timeout = TimeSpan.FromSeconds(30),
            TerminationWaitTimeout = TimeSpan.FromSeconds(5)
        };
        var runTask = runner.RunAsync(request, cancellation.Token);
        await WaitForReadyAsync(parentReadyPath, runTask);
        Assert.True(File.Exists(childReadyPath));
        var childPid = int.Parse(
            await File.ReadAllTextAsync(parentReadyPath),
            System.Globalization.CultureInfo.InvariantCulture);

        cancellation.Cancel();
        var result = await runTask;

        Assert.True(result.Cancelled);
        Assert.False(result.KillFailed);
        Assert.True(WaitForProcessExit(childPid, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Run_ReportsTerminationFailureWithoutHidingTimeout()
    {
        using var temp = new TemporaryDirectory();
        var readyPath = Path.Combine(temp.Path, "termination.ready");
        var runner = new ReportingTerminationFailureRunner();
        var runTask = runner.RunAsync(WaitRequest(readyPath, TimeSpan.FromSeconds(2)));
        await WaitForReadyAsync(readyPath, runTask);

        var result = await runTask;

        Assert.True(result.TimedOut);
        Assert.True(result.KillFailed);
        Assert.Contains("Simulated termination diagnostic", result.TerminationError);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Run_InheritedConsolePreservesExitCodeWithoutCapturingSensitiveArguments()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(new ProcessRequest
        {
            ExecutablePath = ChildExecutablePath,
            Arguments = ["exit", "17"],
            IoMode = ProcessIoMode.InheritConsole,
            CreateNoWindow = false,
            IncludeArgumentsInResult = false
        });

        Assert.Equal(17, result.ExitCode);
        Assert.Empty(result.Arguments);
        Assert.Empty(result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task Run_NullEnvironmentValueRemovesInheritedVariable()
    {
        var variableName = $"GITKEYROUTER_TEST_{Guid.NewGuid():N}";
        var previousValue = Environment.GetEnvironmentVariable(variableName);
        Environment.SetEnvironmentVariable(variableName, "inherited-secret");
        try
        {
            var runner = new ProcessRunner();
            var result = await runner.RunAsync(new ProcessRequest
            {
                ExecutablePath = ChildExecutablePath,
                Arguments = ["print-env", variableName],
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    [variableName] = null
                }
            });

            Assert.True(result.Succeeded);
            Assert.Equal("<null>", result.StandardOutput.Trim());
            Assert.DoesNotContain("inherited-secret", result.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previousValue);
        }
    }

    private static ProcessRequest WaitRequest(string readyPath, TimeSpan timeout)
        => new()
        {
            ExecutablePath = ChildExecutablePath,
            Arguments = ["wait-ready", readyPath],
            Timeout = timeout,
            TerminationWaitTimeout = TimeSpan.FromSeconds(5)
        };

    private static string ChildExecutablePath
    {
        get
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "process-test-child",
                "GitKeyRouter.ProcessTestChild.exe");
            return File.Exists(path)
                ? path
                : throw new FileNotFoundException("The deterministic process test child was not copied.", path);
        }
    }

    private static async Task WaitForReadyAsync(string readyPath, Task<ProcessResult> runTask)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!File.Exists(readyPath))
        {
            if (runTask.IsCompleted)
            {
                var result = await runTask.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"The process ended before its ready handshake. Exit={result.ExitCode}; stderr={result.StandardError}");
            }

            if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException($"The process did not create its ready handshake: {readyPath}");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited || process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed class ReportingTerminationFailureRunner : ProcessRunner
    {
        protected override async Task<string?> TerminateProcessAsync(Process process, TimeSpan waitTimeout)
        {
            await base.TerminateProcessAsync(process, waitTimeout);
            return "Simulated termination diagnostic.";
        }
    }
}
