using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Hikyaku.Core;

public sealed record PtyStartRequest(
    string ShellPath,
    string WorkingDirectory,
    int Columns,
    int Rows,
    string? Arguments = null);

public interface IPtySession : IAsyncDisposable
{
    string WorkingDirectory { get; }
    event Action<string>? OutputReceived;
    event Action<int?>? Exited;
    void Write(string data);
    void Resize(int columns, int rows);
    void Stop();
    /// <summary>Start I/O pumps after OutputReceived/Exited handlers are subscribed.</summary>
    void Begin();
}

public interface IPtySessionFactory
{
    Task<IPtySession> StartAsync(PtyStartRequest request, CancellationToken ct = default);
}

/// <summary>Windows ConPTY-backed interactive session (kernel32 CreatePseudoConsole).</summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtySessionFactory : IPtySessionFactory
{
    public Task<IPtySession> StartAsync(PtyStartRequest request, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ConPTY requires Windows.");
        if (!File.Exists(request.ShellPath))
            throw new FileNotFoundException("Shell executable not found.", request.ShellPath);
        if (!Directory.Exists(request.WorkingDirectory))
            throw new DirectoryNotFoundException("Working directory not found: " + request.WorkingDirectory);
        if (!TrustedShellDiscovery.IsTrustedInstalled(request.ShellPath))
            throw new InvalidOperationException("Shell is not in the trusted installed set.");

        ct.ThrowIfCancellationRequested();
        var session = ConPtySession.Start(request);
        return Task.FromResult<IPtySession>(session);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class ConPtySession : IPtySession
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartFUseStdHandles = 0x00000100;
    private static readonly IntPtr PseudoConsoleAttribute = new(0x00020016);

    private readonly IntPtr _hPC;
    private readonly Process _process;
    private readonly IntPtr _attributeList;
    private readonly IntPtr _hProcess;
    private readonly IntPtr _hThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Stream? _inputStream;
    private Stream? _outputStream;
    private Task? _readLoopTask;
    private bool _stopped;
    private bool _begun;
    private int? _exitCode;
    private static int s_consoleDetached;

    public string WorkingDirectory { get; }
    public event Action<string>? OutputReceived;
    public event Action<int?>? Exited;

    private ConPtySession(
        IntPtr hPC,
        Stream inputStream,
        Stream outputStream,
        Process process,
        IntPtr attributeList,
        IntPtr hProcess,
        IntPtr hThread,
        string workingDirectory)
    {
        _hPC = hPC;
        _inputStream = inputStream;
        _outputStream = outputStream;
        _process = process;
        _attributeList = attributeList;
        _hProcess = hProcess;
        _hThread = hThread;
        WorkingDirectory = workingDirectory;
        // Read/exit pumps start in Begin() after callers subscribe handlers.
    }

    public void Begin()
    {
        lock (_gate)
        {
            if (_begun || _stopped) return;
            _begun = true;
        }
        _readLoopTask = Task.Run(ReadLoopAsync);
        _ = Task.Run(WaitExitAsync);
    }

    public static ConPtySession Start(PtyStartRequest request)
    {
        // Console hosts (tests): detach once so a child cannot steal the host console.
        // Process-wide — do not repeat under parallel ConPTY starts (FreeConsole side effects).
        // WinExe WPF hosts typically have no console; first call is then a no-op failure.
        if (Interlocked.Exchange(ref s_consoleDetached, 1) == 0)
            _ = Native.FreeConsole();

        if (!Native.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe input failed");
        if (!Native.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe output failed");
        }

        var size = new Native.COORD
        {
            X = (short)Math.Clamp(request.Columns, 1, 999),
            Y = (short)Math.Clamp(request.Rows, 1, 999)
        };
        var hr = Native.CreatePseudoConsole(size, inputRead, outputWrite, 0, out var hPC);
        if (hr != 0)
        {
            inputRead.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            outputWrite.Dispose();
            throw new Win32Exception(hr, "CreatePseudoConsole failed");
        }

        // EchoCon: PTY-side ends are duplicated into conhost — close our copies now.
        inputRead.Dispose();
        outputWrite.Dispose();

        Native.PROCESS_INFORMATION pi;
        IntPtr attributeList;
        Process process;
        try
        {
            var cmdline = Quote(request.ShellPath)
                + (string.IsNullOrWhiteSpace(request.Arguments) ? "" : " " + request.Arguments);
            (process, pi, attributeList) = StartProcessAttached(hPC, cmdline, request.WorkingDirectory);
        }
        catch
        {
            try { Native.ClosePseudoConsole(hPC); } catch { /* ignore */ }
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }

        Stream inputStream = new FileStream(inputWrite, FileAccess.Write);
        Stream outputStream = new FileStream(outputRead, FileAccess.Read);
        return new ConPtySession(
            hPC, inputStream, outputStream, process,
            attributeList, pi.hProcess, pi.hThread, request.WorkingDirectory);
    }

    public void Write(string data)
    {
        lock (_gate)
        {
            if (_stopped || _inputStream is null) return;
            var bytes = Encoding.UTF8.GetBytes(data);
            _inputStream.Write(bytes, 0, bytes.Length);
            _inputStream.Flush();
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            if (_stopped || _hPC == IntPtr.Zero) return;
            var size = new Native.COORD
            {
                X = (short)Math.Clamp(columns, 1, 999),
                Y = (short)Math.Clamp(rows, 1, 999)
            };
            _ = Native.ResizePseudoConsole(_hPC, size);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
        }
        try { _cts.Cancel(); } catch { /* ignore */ }
        try
        {
            if (!_process.HasExited)
            {
                try { _process.Kill(entireProcessTree: true); }
                catch
                {
                    try { _process.Kill(); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }

        try { Native.ClosePseudoConsole(_hPC); } catch { /* ignore */ }
        try { _inputStream?.Dispose(); } catch { /* ignore */ }
        _inputStream = null;
        try { _outputStream?.Dispose(); } catch { /* ignore */ }
        _outputStream = null;
        if (_attributeList != IntPtr.Zero)
        {
            try { Native.DeleteProcThreadAttributeList(_attributeList); } catch { /* ignore */ }
            try { Marshal.FreeHGlobal(_attributeList); } catch { /* ignore */ }
        }
        if (_hThread != IntPtr.Zero) try { Native.CloseHandle(_hThread); } catch { /* ignore */ }
        if (_hProcess != IntPtr.Zero) try { Native.CloseHandle(_hProcess); } catch { /* ignore */ }

        var code = _exitCode;
        try { if (_process.HasExited) code = _process.ExitCode; } catch { /* ignore */ }
        Exited?.Invoke(code);
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        _cts.Dispose();
        try { _process.Dispose(); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[4096];
        try
        {
            var stream = _outputStream ?? throw new ObjectDisposedException(nameof(ConPtySession));
            while (!_cts.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), _cts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                var text = Encoding.UTF8.GetString(buffer, 0, n);
                OutputReceived?.Invoke(text);
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (ObjectDisposedException) { /* expected */ }
        catch (IOException) { /* pipe closed */ }
    }

    private async Task WaitExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync(_cts.Token).ConfigureAwait(false);
            _exitCode = _process.ExitCode;
        }
        catch { /* ignore */ }

        lock (_gate)
        {
            if (_stopped) return;
        }

        // Leave ConPTY open and prefer EOF drain so final output precedes Exited.
        // Bounded — controller still accepts late ownership-matched chunks until Close.
        var read = _readLoopTask;
        if (read is not null)
        {
            try
            {
                await read.WaitAsync(TimeSpan.FromMilliseconds(750), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* expected */ }
            catch (TimeoutException) { /* bounded drain */ }
        }

        lock (_gate)
        {
            if (_stopped) return;
        }
        Exited?.Invoke(_exitCode);
    }

    private static (Process Process, Native.PROCESS_INFORMATION Info, IntPtr AttributeList) StartProcessAttached(
        IntPtr hPC, string commandLine, string cwd)
    {
        var lpSize = IntPtr.Zero;
        var success = Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        if (success || lpSize == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not size proc thread attribute list");

        var attributeList = Marshal.AllocHGlobal(lpSize);
        try
        {
            success = Native.InitializeProcThreadAttributeList(attributeList, 1, 0, ref lpSize);
            if (!success)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");

            // MiniTerm/EchoCon: pass HPCON handle value as lpValue (not &hPC).
            success = Native.UpdateProcThreadAttribute(
                attributeList,
                0,
                PseudoConsoleAttribute,
                hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero);
            if (!success)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

            var startupInfo = new Native.STARTUPINFOEX();
            startupInfo.StartupInfo.cb = (uint)Marshal.SizeOf<Native.STARTUPINFOEX>();
            startupInfo.StartupInfo.dwFlags = StartFUseStdHandles;
            startupInfo.lpAttributeList = attributeList;

            var pSi = Marshal.AllocHGlobal(Marshal.SizeOf<Native.STARTUPINFOEX>());
            try
            {
                Marshal.StructureToPtr(startupInfo, pSi, false);
                var cmd = new StringBuilder(commandLine);
                var cwdPtr = string.IsNullOrWhiteSpace(cwd) ? IntPtr.Zero : Marshal.StringToHGlobalUni(cwd);
                try
                {
                    success = Native.CreateProcessW(
                        IntPtr.Zero,
                        cmd,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                        IntPtr.Zero,
                        cwdPtr,
                        pSi,
                        out var pi);
                    if (!success)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed for " + commandLine);

                    var process = Process.GetProcessById((int)pi.dwProcessId);
                    return (process, pi, attributeList);
                }
                finally
                {
                    if (cwdPtr != IntPtr.Zero) Marshal.FreeHGlobal(cwdPtr);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pSi);
            }
        }
        catch
        {
            try { Native.DeleteProcThreadAttributeList(attributeList); } catch { /* ignore */ }
            Marshal.FreeHGlobal(attributeList);
            throw;
        }
    }

    private static string Quote(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? "\"" + path + "\"" : path;

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct STARTUPINFO
        {
            public uint cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public uint dwX;
            public uint dwY;
            public uint dwXSize;
            public uint dwYSize;
            public uint dwXCountChars;
            public uint dwYCountChars;
            public uint dwFillAttribute;
            public uint dwFlags;
            public ushort wShowWindow;
            public ushort cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue,
            IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(
            IntPtr lpApplicationName,
            StringBuilder lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            IntPtr lpCurrentDirectory,
            IntPtr lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);
    }
}
