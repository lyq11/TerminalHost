using System;
using System.Threading.Tasks;
using System.Windows;
using TerminalHost.Core;
using TerminalHost.Infrastructure;

namespace TerminalHost;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (Array.Exists(e.Args, argument =>
                string.Equals(argument, "--mcp-stdio", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunStdioMcpAsync();
            return;
        }

        _singleInstanceMutex = new Mutex(true, @"Local\TerminalHost.Gui.Singleton", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("TerminalHost 已经在运行。", "TerminalHost", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
        AppLog.Info("TerminalHost GUI 已启动。");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("TerminalHost GUI 已退出。");
        if (_singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private async Task RunStdioMcpAsync()
    {
        int exitCode;
        try
        {
            exitCode = await BundledMcpHost.RunStdioAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            exitCode = 1;
        }

        Shutdown(exitCode);
    }
}
