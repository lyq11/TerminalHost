using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TerminalHost.Core;

internal sealed class BundledMcpHost : IDisposable
{
    private readonly Process _process;

    private BundledMcpHost(Process process)
    {
        _process = process;
    }

    public static BundledMcpHost? TryStartHttp()
    {
        if (!TryResolveBundle(out string nodePath, out string serverPath))
            return null;

        Process process = StartNode(nodePath, serverPath, redirectStreams: true,
            "--transport", "http", "--host", "127.0.0.1", "--port", "8766");

        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new BundledMcpHost(process);
    }

    public static async Task<int> RunStdioAsync()
    {
        if (!TryResolveBundle(out string nodePath, out string serverPath))
            throw new FileNotFoundException("The bundled MCP runtime was not found. Reinstall the complete TerminalHost package.");

        using Process process = StartNode(nodePath, serverPath, redirectStreams: true,
            "--transport", "stdio");
        using var cancellation = new CancellationTokenSource();

        Task input = CopyIgnoringCancellationAsync(
            Console.OpenStandardInput(), process.StandardInput.BaseStream, cancellation.Token, closeDestination: true);
        Task output = CopyIgnoringCancellationAsync(
            process.StandardOutput.BaseStream, Console.OpenStandardOutput(), cancellation.Token);
        Task error = CopyIgnoringCancellationAsync(
            process.StandardError.BaseStream, Console.OpenStandardError(), cancellation.Token);

        await process.WaitForExitAsync();
        cancellation.Cancel();
        await Task.WhenAll(input, output, error);
        return process.ExitCode;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static Process StartNode(
        string nodePath,
        string serverPath,
        bool redirectStreams,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            WorkingDirectory = Path.GetDirectoryName(serverPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = redirectStreams,
            RedirectStandardOutput = redirectStreams,
            RedirectStandardError = redirectStreams
        };
        startInfo.ArgumentList.Add(serverPath);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the bundled TerminalHost MCP server.");
    }

    private static bool TryResolveBundle(out string nodePath, out string serverPath)
    {
        var candidates = new List<string> { AppContext.BaseDirectory };
        DirectoryInfo? parent = Directory.GetParent(AppContext.BaseDirectory);
        if (parent is not null)
            candidates.Add(parent.FullName);

        foreach (string root in candidates)
        {
            string node = Path.Combine(root, "runtime", "node.exe");
            string server = Path.Combine(root, "mcp", "dist", "src", "index.js");
            if (File.Exists(node) && File.Exists(server))
            {
                nodePath = node;
                serverPath = server;
                return true;
            }
        }

        nodePath = string.Empty;
        serverPath = string.Empty;
        return false;
    }

    private static async Task CopyIgnoringCancellationAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken,
        bool closeDestination = false)
    {
        try
        {
            await source.CopyToAsync(destination, 81920, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            if (closeDestination)
                destination.Close();
        }
    }
}
