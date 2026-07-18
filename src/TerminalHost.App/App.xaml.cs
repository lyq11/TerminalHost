using System;
using System.Threading.Tasks;
using System.Windows;
using TerminalHost.Core;

namespace TerminalHost;

public partial class App : Application
{
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

        MainWindow = new MainWindow();
        MainWindow.Show();
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
