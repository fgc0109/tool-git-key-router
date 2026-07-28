using System.Diagnostics;

return await ProcessTestChild.RunAsync(args);

internal static class ProcessTestChild
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("A mode is required.");
            return 2;
        }

        switch (args[0])
        {
            case "emit-lines" when args.Length == 2 && int.TryParse(args[1], out var lineCount):
                for (var index = 1; index <= lineCount; index++)
                {
                    Console.Out.WriteLine($"stdout-{index}");
                    Console.Error.WriteLine($"stderr-{index}");
                }

                return 0;

            case "emit-line" when args.Length == 2 && int.TryParse(args[1], out var characterCount):
                Console.Out.WriteLine(new string('x', characterCount));
                return 0;

            case "wait-ready" when args.Length == 2:
                WriteReady(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                return 0;

            case "spawn-tree" when args.Length == 3:
                return await SpawnTreeAsync(args[1], args[2]).ConfigureAwait(false);

            case "print-env" when args.Length == 2:
                Console.Out.WriteLine(
                    Environment.GetEnvironmentVariable(args[1]) ?? "<null>");
                return 0;

            case "exit" when args.Length == 2
                && int.TryParse(args[1], out var exitCode):
                return exitCode;

            default:
                Console.Error.WriteLine($"Invalid mode or arguments: {string.Join(' ', args)}");
                return 2;
        }
    }

    private static async Task<int> SpawnTreeAsync(string parentReadyPath, string childReadyPath)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The child executable path is unavailable.");
        using var child = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "wait-ready", childReadyPath }
        }) ?? throw new InvalidOperationException("Failed to start the nested process.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(childReadyPath))
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }

        WriteReady(parentReadyPath, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await child.WaitForExitAsync().ConfigureAwait(false);
        return child.ExitCode;
    }

    private static void WriteReady(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, fullPath);
    }
}
