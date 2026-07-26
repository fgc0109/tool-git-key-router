using System.Diagnostics;
using GitKeyRouter.Core.Abstractions;
using GitKeyRouter.Core.Models;

namespace GitKeyRouter.Infrastructure.ProcessExecution;

public class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var output = new BoundedLineBuffer(
            request.MaxOutputLines,
            request.MaxOutputCharactersPerLine);
        var errors = new BoundedLineBuffer(
            request.MaxOutputLines,
            request.MaxOutputCharactersPerLine);

        using var process = new Process
        {
            StartInfo = CreateStartInfo(request),
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                output.Add(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                errors.Add(eventArgs.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start '{request.ExecutablePath}'.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new ProcessResult
            {
                ExecutablePath = request.ExecutablePath,
                Arguments = request.Arguments,
                Duration = stopwatch.Elapsed,
                StartException = exception,
                StandardError = exception.Message
            };
        }

        using var timeoutSource = new CancellationTokenSource(request.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var timedOut = false;
        var cancelled = false;
        string? terminationError = null;
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = cancellationToken.IsCancellationRequested;
            terminationError = await TerminateProcessAsync(
                process,
                request.TerminationWaitTimeout).ConfigureAwait(false);
            if (process.HasExited)
            {
                process.WaitForExit();
            }
        }

        stopwatch.Stop();
        return new ProcessResult
        {
            ExecutablePath = request.ExecutablePath,
            Arguments = request.Arguments,
            ExitCode = process.HasExited ? process.ExitCode : null,
            StandardOutput = output.GetText(),
            StandardError = errors.GetText(),
            StandardOutputTruncated = output.Truncated,
            StandardErrorTruncated = errors.Truncated,
            TimedOut = timedOut,
            Cancelled = cancelled,
            KillFailed = terminationError is not null,
            TerminationError = terminationError,
            Duration = stopwatch.Elapsed
        };
    }

    protected virtual async Task<string?> TerminateProcessAsync(Process process, TimeSpan waitTimeout)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception exception)
        {
            return $"Failed to terminate the process tree: {exception.Message}";
        }

        if (process.HasExited)
        {
            return null;
        }

        try
        {
            using var waitSource = new CancellationTokenSource(
                waitTimeout > TimeSpan.Zero ? waitTimeout : TimeSpan.FromMilliseconds(1));
            await process.WaitForExitAsync(waitSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return $"The process tree did not exit within {waitTimeout}.";
        }
        catch (Exception exception)
        {
            return $"Failed while waiting for the terminated process tree: {exception.Message}";
        }

        return process.HasExited
            ? null
            : $"The process tree did not exit within {waitTimeout}.";
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = request.CreateNoWindow,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in request.EnvironmentVariables)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return startInfo;
    }

    private sealed class BoundedLineBuffer
    {
        private const string OmissionMarkerFormat = "[... {0} line(s) omitted ...]";
        private const string LineMarker = "[... line truncated ...]";
        private readonly object _gate = new();
        private readonly int _headLimit;
        private readonly int _tailLimit;
        private readonly int _maxCharactersPerLine;
        private readonly List<string> _head = [];
        private readonly Queue<string> _tail = [];
        private long _totalLines;
        private bool _linesDropped;
        private bool _lineTruncated;

        public BoundedLineBuffer(int maxLines, int maxCharactersPerLine)
        {
            maxLines = Math.Max(2, maxLines);
            _maxCharactersPerLine = Math.Max(64, maxCharactersPerLine);
            _headLimit = maxLines / 2;
            _tailLimit = maxLines - _headLimit;
        }

        public bool Truncated
        {
            get
            {
                lock (_gate)
                {
                    return _linesDropped || _lineTruncated;
                }
            }
        }

        public void Add(string line)
        {
            lock (_gate)
            {
                _totalLines++;
                var retainedLine = LimitLine(line);
                if (_head.Count < _headLimit)
                {
                    _head.Add(retainedLine);
                    return;
                }

                if (_tail.Count == _tailLimit)
                {
                    _tail.Dequeue();
                    _linesDropped = true;
                }

                _tail.Enqueue(retainedLine);
            }
        }

        public string GetText()
        {
            lock (_gate)
            {
                var lines = new List<string>(_head.Count + _tail.Count + 1);
                lines.AddRange(_head);
                if (_linesDropped)
                {
                    var omitted = _totalLines - _head.Count - _tail.Count;
                    lines.Add(string.Format(OmissionMarkerFormat, omitted));
                }

                lines.AddRange(_tail);
                return string.Join(Environment.NewLine, lines);
            }
        }

        private string LimitLine(string line)
        {
            if (line.Length <= _maxCharactersPerLine)
            {
                return line;
            }

            _lineTruncated = true;
            var available = _maxCharactersPerLine - LineMarker.Length;
            var prefixLength = available / 2;
            var suffixLength = available - prefixLength;
            return line[..prefixLength] + LineMarker + line[^suffixLength..];
        }
    }
}
