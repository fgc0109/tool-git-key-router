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
            ? SingleInstanceGuard.TryAcquire()
            : null;
        if (instanceGuard is { IsPrimaryInstance: false })
        {
            return ReportExistingInstance(args.Length > 0);
        }

        ApplicationServices? services = null;
        try
        {
            services = AppBootstrapper.CreateServices();
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
            Application.Run(new MainForm(services));
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
