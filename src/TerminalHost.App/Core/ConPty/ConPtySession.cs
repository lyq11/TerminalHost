using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TerminalHost.Core.ConPty;

internal sealed class ConPtySession : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<uint> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FileStream? _input;
    private FileStream? _output;
    private IntPtr _pseudoConsole;
    private IntPtr _processHandle;
    private Task? _outputTask;
    private Task? _waitTask;
    private int _begun;
    private int _stopping;
    private int _disposed;

    internal event Action<string>? Output;
    internal event Action<uint>? Exited;

    internal bool IsRunning => !_exitTcs.Task.IsCompleted;
    internal Task<uint> ExitTask => _exitTcs.Task;

    internal static ConPtySession CreatePaused(string commandLine, string workingDirectory, int columns, int rows)
    {
        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr pseudoConsole = IntPtr.Zero;
        NativeMethods.PROCESS_INFORMATION processInfo = default;

        try
        {
            if (!NativeMethods.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0))
                NativeMethods.ThrowLastWin32("CreatePipe(input) failed");
            if (!NativeMethods.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                NativeMethods.ThrowLastWin32("CreatePipe(output) failed");

            var hr = NativeMethods.CreatePseudoConsole(
                new NativeMethods.COORD(columns, rows), inputRead, outputWrite, 0, out pseudoConsole);
            NativeMethods.ThrowIfFailedHResult(hr, "CreatePseudoConsole failed");

            var startupInfo = new NativeMethods.STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();

            var attributeListSize = IntPtr.Zero;
            _ = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(attributeListSize);

            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                NativeMethods.ThrowLastWin32("InitializeProcThreadAttributeList failed");

            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    pseudoConsole,
                    new IntPtr(IntPtr.Size),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                NativeMethods.ThrowLastWin32("UpdateProcThreadAttribute failed");
            }

            startupInfo.lpAttributeList = attributeList;
            var mutableCommandLine = new StringBuilder(commandLine);
            var flags = NativeMethods.EXTENDED_STARTUPINFO_PRESENT | NativeMethods.CREATE_UNICODE_ENVIRONMENT;

            if (!NativeMethods.CreateProcessW(
                    null,
                    mutableCommandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    flags,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startupInfo,
                    out processInfo))
            {
                NativeMethods.ThrowLastWin32($"CreateProcessW failed: {commandLine}");
            }

            // ConPTY/conhost already duplicated these two ends. The host must release them after CreateProcess.
            inputRead.Dispose();
            inputRead = null;
            outputWrite.Dispose();
            outputWrite = null;

            if (processInfo.hThread != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(processInfo.hThread);
                processInfo.hThread = IntPtr.Zero;
            }

            var session = new ConPtySession
            {
                _pseudoConsole = pseudoConsole,
                _processHandle = processInfo.hProcess,
                _input = new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: false),
                _output = new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false)
            };

            inputWrite = null;
            outputRead = null;
            pseudoConsole = IntPtr.Zero;
            processInfo.hProcess = IntPtr.Zero;

            return session;
        }
        catch
        {
            if (processInfo.hThread != IntPtr.Zero) NativeMethods.CloseHandle(processInfo.hThread);
            if (processInfo.hProcess != IntPtr.Zero) NativeMethods.CloseHandle(processInfo.hProcess);
            if (pseudoConsole != IntPtr.Zero) NativeMethods.ClosePseudoConsole(pseudoConsole);
            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    internal void Begin()
    {
        if (Interlocked.Exchange(ref _begun, 1) != 0) return;
        _outputTask = Task.Run(PumpOutputAsync);
        _waitTask = Task.Run(WaitForExit);
    }

    internal async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(data)) return;
        var input = _input ?? throw new ObjectDisposedException(nameof(ConPtySession));
        var bytes = Encoding.UTF8.GetBytes(data);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal void Resize(int columns, int rows)
    {
        var handle = _pseudoConsole;
        if (handle == IntPtr.Zero) return;
        var hr = NativeMethods.ResizePseudoConsole(handle, new NativeMethods.COORD(columns, rows));
        NativeMethods.ThrowIfFailedHResult(hr, "ResizePseudoConsole failed");
    }

    internal async Task StopAsync(bool graceful = true)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;

        if (graceful && IsRunning)
        {
            try
            {
                await WriteAsync("exit\r").ConfigureAwait(false);
                var completed = await Task.WhenAny(_exitTcs.Task, Task.Delay(700)).ConfigureAwait(false);
                if (completed == _exitTcs.Task) return;
            }
            catch
            {
                // Fall through to closing the pseudoconsole.
            }
        }

        ClosePseudoConsoleOnce();
        var waitResult = await Task.WhenAny(_exitTcs.Task, Task.Delay(2000)).ConfigureAwait(false);
        if (waitResult != _exitTcs.Task)
        {
            var process = _processHandle;
            if (process != IntPtr.Zero) NativeMethods.TerminateProcess(process, 1);
        }
    }

    private async Task PumpOutputAsync()
    {
        var output = _output;
        if (output is null) return;

        var byteBuffer = new byte[16 * 1024];
        var charBuffer = new char[Encoding.UTF8.GetMaxCharCount(byteBuffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();

        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var count = await output.ReadAsync(byteBuffer, _lifetime.Token).ConfigureAwait(false);
                if (count == 0) break;

                var chars = decoder.GetChars(byteBuffer, 0, count, charBuffer, 0, flush: false);
                if (chars > 0)
                {
                    try { Output?.Invoke(new string(charBuffer, 0, chars)); } catch { }
                }
            }

            var remaining = decoder.GetChars(Array.Empty<byte>(), 0, 0, charBuffer, 0, flush: true);
            if (remaining > 0)
            {
                try { Output?.Invoke(new string(charBuffer, 0, remaining)); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException) when (_disposed != 0)
        {
        }
    }

    private void WaitForExit()
    {
        uint exitCode = 1;
        try
        {
            var process = _processHandle;
            if (process == IntPtr.Zero) return;
            var wait = NativeMethods.WaitForSingleObject(process, uint.MaxValue);
            if (wait == NativeMethods.WAIT_FAILED) NativeMethods.ThrowLastWin32("WaitForSingleObject failed");
            if (wait == NativeMethods.WAIT_OBJECT_0 && !NativeMethods.GetExitCodeProcess(process, out exitCode))
                NativeMethods.ThrowLastWin32("GetExitCodeProcess failed");
        }
        catch
        {
            exitCode = 1;
        }
        finally
        {
            _exitTcs.TrySetResult(exitCode);
            try { Exited?.Invoke(exitCode); } catch { }
            ClosePseudoConsoleOnce();
        }
    }

    private void ClosePseudoConsoleOnce()
    {
        var handle = Interlocked.Exchange(ref _pseudoConsole, IntPtr.Zero);
        if (handle != IntPtr.Zero) NativeMethods.ClosePseudoConsole(handle);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Begin();
        try { await StopAsync(graceful: false).ConfigureAwait(false); } catch { }
        _lifetime.Cancel();

        if (_outputTask is not null)
            await Task.WhenAny(_outputTask, Task.Delay(1000)).ConfigureAwait(false);

        _input?.Dispose();
        _output?.Dispose();

        var process = Interlocked.Exchange(ref _processHandle, IntPtr.Zero);
        if (process != IntPtr.Zero) NativeMethods.CloseHandle(process);

        _writeLock.Dispose();
        _lifetime.Dispose();
    }
}
