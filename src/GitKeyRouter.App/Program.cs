using GitKeyRouter.App.Cli;
using GitKeyRouter.App.Forms;
using GitKeyRouter.App.Presentation;

namespace GitKeyRouter.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (CliApplication.CanRunWithoutServices(args))
        {
            ConsoleBridge.Attach();
            return CliApplication.RunWithoutServices(args);
        }

        using var instanceGuard = CliApplication.RequiresExclusiveInstance(args)
            ? SingleInstanceGuard.TryAcquire(signalExistingInstance: args.Length == 0)
            : null;
        if (instanceGuard is { IsPrimaryInstance: false })
        {
            return args.Length == 0 ? 0 : ReportExistingInstance(isCli: true);
        }

        ApplicationServices? services = null;
        try
        {
            services = AppBootstrapper.CreateServices();
            if (instanceGuard is { IsPrimaryInstance: true })
            {
                var recovery = services.GitProfileService
                    .RecoverInterruptedTransactionsAsync()
                    .GetAwaiter()
                    .GetResult();
                if (!recovery.Success)
                {
                    throw new InvalidOperationException(
                        string.Join(Environment.NewLine, new[] { recovery.Message }.Concat(recovery.Errors)));
                }
            }

            if (args.Length > 0)
            {
                ConsoleBridge.Attach();
                return new CliApplication(services).RunAsync(args).GetAwaiter().GetResult();
            }

            var startupConfig = services.ConfigStore.LoadAsync().GetAwaiter().GetResult();
            AppLocalization.SetLanguage(AppLocalization.Parse(startupConfig.UiLanguage));
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, eventArgs) =>
            {
                services.Logger.Error("Unhandled UI thread error.", eventArgs.Exception);
                MessageBox.Show(eventArgs.Exception.ToString(), "GitKeyRouter - 未处理错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            using var mainForm = new MainForm(services);
            if (instanceGuard is { IsPrimaryInstance: true })
            {
                _ = mainForm.Handle;
                instanceGuard.StartListening(() =>
                {
                    if (mainForm.IsDisposed || mainForm.Disposing || !mainForm.IsHandleCreated)
                    {
                        return;
                    }

                    try
                    {
                        mainForm.BeginInvoke(new Action(mainForm.ActivateFromSecondaryInstance));
                    }
                    catch (ObjectDisposedException)
                    {
                        // The application is already closing.
                    }
                    catch (InvalidOperationException)
                    {
                        // The window handle can disappear while shutdown is in progress.
                    }
                });
            }

            Application.Run(mainForm);
            return 0;
        }
        catch (Exception exception)
        {
            services?.Logger.Error("Fatal application startup error.", exception);
            if (args.Length > 0)
            {
                ConsoleBridge.Attach();
                Console.Error.WriteLine(exception);
            }
            else
            {
                MessageBox.Show(
                    exception.ToString(),
                    "GitKeyRouter - 启动失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return 3;
        }
    }

    private static int ReportExistingInstance(bool isCli)
    {
        const string message = "GitKeyRouter 已在当前 Windows 用户下运行。\n\nGitKeyRouter is already running for this Windows user.";
        if (isCli)
        {
            ConsoleBridge.Attach();
            Console.Error.WriteLine(message.Replace("\n\n", Environment.NewLine, StringComparison.Ordinal));
        }
        else
        {
            ApplicationConfiguration.Initialize();
            MessageBox.Show(
                message,
                "GitKeyRouter",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        return 4;
    }
}
