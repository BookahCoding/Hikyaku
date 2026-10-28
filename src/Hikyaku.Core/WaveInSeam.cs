using System.Runtime.InteropServices;

namespace Hikyaku.Core;

/// <summary>WAVEFORMATEX layout — must match winmm (18 bytes, packed).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WaveFormatEx
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

/// <summary>Monotonic clock for capture watchdog — production uses wall clock; tests inject fakes.</summary>
public interface ICaptureClock
{
    long ElapsedMilliseconds { get; }
    void Sleep(int milliseconds);
}

public sealed class WallCaptureClock : ICaptureClock
{
    public long ElapsedMilliseconds => Environment.TickCount64;
    public void Sleep(int milliseconds) => Thread.Sleep(milliseconds);
}

public sealed class FakeCaptureClock : ICaptureClock
{
    private long _ms;
    public long ElapsedMilliseconds => Interlocked.Read(ref _ms);
    public void Advance(long deltaMs) => Interlocked.Add(ref _ms, deltaMs);
    public void Sleep(int milliseconds)
    {
        // Wait until a test Advances wall time (or safety timeout) — does not claim real device timing.
        var target = ElapsedMilliseconds + Math.Max(1, milliseconds);
        var spins = 0;
        while (ElapsedMilliseconds < target && spins++ < 5_000)
            Thread.Sleep(1);
    }
}

/// <summary>One prepared capture buffer owned by the backend.</summary>
public sealed class WaveInBufferToken
{
    public int Id { get; init; }
    public int SizeBytes { get; init; }
    public byte[] Scratch { get; init; } = Array.Empty<byte>();
    public uint BytesRecorded { get; set; }
    public bool Prepared { get; set; }
    public bool InQueue { get; set; }
}

public readonly record struct WaveInDataEvent(int SessionId, int Handle, WaveInBufferToken Buffer, uint BytesRecorded);

/// <summary>
/// Injectable winmm seam. Production <see cref="WinMmWaveInBackend"/> talks to real devices;
/// tests use <see cref="FakeWaveInBackend"/> — never claim hardware from fakes.
/// </summary>
public interface IWaveInBackend
{
    int GetNumDevs();
    int Open(in WaveFormatEx format, int sessionId, out int handle);
    int PrepareHeader(int handle, WaveInBufferToken buffer);
    int UnprepareHeader(int handle, WaveInBufferToken buffer);
    int AddBuffer(int handle, WaveInBufferToken buffer);
    int Start(int handle);
    int Stop(int handle);
    int Reset(int handle);
    int Close(int handle);
    WaveInBufferToken CreateBuffer(int sizeBytes);
    void FreeBuffer(WaveInBufferToken buffer);
    event Action<WaveInDataEvent>? DataAvailable;
}

/// <summary>Deterministic fake — no real microphone. Does not prove hardware behaviour.</summary>
public sealed class FakeWaveInBackend : IWaveInBackend
{
    private readonly object _gate = new();
    private int _nextHandle = 1;
    private int _nextBuf = 1;
    private int _nextSession;
    private readonly Dictionary<int, int> _handleSession = new();
    private readonly HashSet<int> _open = new();
    private readonly HashSet<WaveInBufferToken> _alive = new();
    private readonly List<Action> _watchdogPumps = new();

    public bool FailOnStart { get; set; }
    public bool FailOnPrepareSecondBuffer { get; set; }
    public bool EmitDataAutomatically { get; set; } = true;
    public int LastOpenSessionId { get; private set; }
    public int OpenHandlesAlive { get { lock (_gate) return _open.Count; } }
    public int PreparedBuffersAlive
    {
        get { lock (_gate) return _alive.Count(b => b.Prepared); }
    }

    public event Action<WaveInDataEvent>? DataAvailable;

    public void RegisterWatchdogPump(Action pump)
    {
        lock (_gate) _watchdogPumps.Add(pump);
    }

    public void PumpWatchdogOpportunity()
    {
        Action[] pumps;
        lock (_gate) pumps = _watchdogPumps.ToArray();
        foreach (var p in pumps) p();
    }

    public int GetNumDevs() => 1;

    public int Open(in WaveFormatEx format, int sessionId, out int handle)
    {
        lock (_gate)
        {
            handle = _nextHandle++;
            _open.Add(handle);
            _handleSession[handle] = sessionId;
            LastOpenSessionId = sessionId;
            _nextSession = sessionId;
            return 0;
        }
    }

    public int PrepareHeader(int handle, WaveInBufferToken buffer)
    {
        lock (_gate)
        {
            if (FailOnPrepareSecondBuffer && _alive.Count(b => b.Prepared) >= 1)
                return 7; // MMSYSERR_NOMEM stand-in
            buffer.Prepared = true;
            return 0;
        }
    }

    public int UnprepareHeader(int handle, WaveInBufferToken buffer)
    {
        buffer.Prepared = false;
        buffer.InQueue = false;
        return 0;
    }

    public int AddBuffer(int handle, WaveInBufferToken buffer)
    {
        buffer.InQueue = true;
        return 0;
    }

    public int Start(int handle)
    {
        if (FailOnStart) return 4; // allocated/busy stand-in
        if (EmitDataAutomatically)
        {
            // One initial empty tick is enough; tests drive EmitData explicitly when needed.
        }
        return 0;
    }

    public int Stop(int handle) => 0;
    public int Reset(int handle) => 0;

    public int Close(int handle)
    {
        lock (_gate)
        {
            _open.Remove(handle);
            _handleSession.Remove(handle);
            return 0;
        }
    }

    public WaveInBufferToken CreateBuffer(int sizeBytes)
    {
        var tok = new WaveInBufferToken
        {
            Id = _nextBuf++,
            SizeBytes = sizeBytes,
            Scratch = new byte[sizeBytes]
        };
        lock (_gate) _alive.Add(tok);
        return tok;
    }

    public void FreeBuffer(WaveInBufferToken buffer)
    {
        buffer.Prepared = false;
        buffer.InQueue = false;
        lock (_gate) _alive.Remove(buffer);
    }

    public void EmitData(int sessionId, byte[] pcm)
    {
        int handle;
        WaveInBufferToken buf;
        lock (_gate)
        {
            handle = _handleSession.FirstOrDefault(kv => kv.Value == sessionId).Key;
            buf = _alive.FirstOrDefault(b => b.InQueue)
                ?? _alive.FirstOrDefault()
                ?? CreateBuffer(Math.Max(320, pcm.Length));
            var n = Math.Min(pcm.Length, buf.SizeBytes);
            if (n > 0)
                Buffer.BlockCopy(pcm, 0, buf.Scratch, 0, n);
            buf.BytesRecorded = (uint)n;
        }
        // Session id is authoritative — capture rejects stale sessions even if bytes arrive.
        DataAvailable?.Invoke(new WaveInDataEvent(sessionId, handle, buf, buf.BytesRecorded));
    }
}

/// <summary>Real winmm — production Windows path only; tests must inject <see cref="FakeWaveInBackend"/>.</summary>
public sealed class WinMmWaveInBackend : IWaveInBackend
{
    private WaveInProc? _proc;
    private int _sessionId;
    private IntPtr _waveIn;
    private readonly Dictionary<IntPtr, NativeBuf> _natives = new();

    public event Action<WaveInDataEvent>? DataAvailable;

    public int GetNumDevs() => waveInGetNumDevs();

    public int Open(in WaveFormatEx format, int sessionId, out int handle)
    {
        _sessionId = sessionId;
        _proc = OnWaveIn;
        var fmt = format;
        var mr = waveInOpen(out _waveIn, 0xFFFFFFFF, ref fmt, _proc, IntPtr.Zero, 0x00030000);
        handle = unchecked((int)_waveIn.ToInt64());
        return mr;
    }

    public int PrepareHeader(int handle, WaveInBufferToken buffer)
        => waveInPrepareHeader(_waveIn, RequireNative(buffer).HeaderPtr, Marshal.SizeOf<WaveHdr>());

    public int UnprepareHeader(int handle, WaveInBufferToken buffer)
        => waveInUnprepareHeader(_waveIn, RequireNative(buffer).HeaderPtr, Marshal.SizeOf<WaveHdr>());

    public int AddBuffer(int handle, WaveInBufferToken buffer)
        => waveInAddBuffer(_waveIn, RequireNative(buffer).HeaderPtr, Marshal.SizeOf<WaveHdr>());

    public int Start(int handle) => waveInStart(_waveIn);
    public int Stop(int handle) => waveInStop(_waveIn);
    public int Reset(int handle) => waveInReset(_waveIn);

    public int Close(int handle)
    {
        var mr = _waveIn == IntPtr.Zero ? 0 : waveInClose(_waveIn);
        _waveIn = IntPtr.Zero;
        return mr;
    }

    public WaveInBufferToken CreateBuffer(int sizeBytes)
    {
        var data = Marshal.AllocHGlobal(sizeBytes);
        var hdrPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>());
        var tok = new WaveInBufferToken { Id = hdrPtr.GetHashCode(), SizeBytes = sizeBytes, Scratch = new byte[sizeBytes] };
        var native = new NativeBuf(data, hdrPtr, sizeBytes, tok);
        _natives[hdrPtr] = native;
        var hdr = new WaveHdr { lpData = data, dwBufferLength = (uint)sizeBytes };
        Marshal.StructureToPtr(hdr, hdrPtr, false);
        return tok;
    }

    public void FreeBuffer(WaveInBufferToken buffer)
    {
        NativeBuf? found = null;
        foreach (var kv in _natives)
        {
            if (ReferenceEquals(kv.Value.Token, buffer)) { found = kv.Value; break; }
        }
        if (found is null) return;
        _natives.Remove(found.HeaderPtr);
        Marshal.FreeHGlobal(found.DataPtr);
        Marshal.FreeHGlobal(found.HeaderPtr);
    }

    private NativeBuf RequireNative(WaveInBufferToken buffer)
    {
        foreach (var kv in _natives)
            if (ReferenceEquals(kv.Value.Token, buffer)) return kv.Value;
        throw new InvalidOperationException("Wave buffer not allocated.");
    }

    private void OnWaveIn(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        // winmm callback restrictions: keep work minimal; no heavy UI — raise DataAvailable only.
        if (uMsg != 0x3C0) return; // WIM_DATA
        if (!_natives.TryGetValue(dwParam1, out var native)) return;
        var hdr = Marshal.PtrToStructure<WaveHdr>(dwParam1);
        var n = (int)hdr.dwBytesRecorded;
        if (n > 0 && hdr.lpData != IntPtr.Zero)
        {
            n = Math.Min(n, native.Token.Scratch.Length);
            Marshal.Copy(hdr.lpData, native.Token.Scratch, 0, n);
        }
        native.Token.BytesRecorded = (uint)n;
        native.Token.InQueue = false;
        DataAvailable?.Invoke(new WaveInDataEvent(_sessionId, unchecked((int)hwi.ToInt64()), native.Token, (uint)n));
    }

    private sealed class NativeBuf(IntPtr data, IntPtr header, int size, WaveInBufferToken token)
    {
        public IntPtr DataPtr { get; } = data;
        public IntPtr HeaderPtr { get; } = header;
        public int Size { get; } = size;
        public WaveInBufferToken Token { get; } = token;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    private delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [DllImport("winmm.dll")] private static extern int waveInGetNumDevs();
    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WaveFormatEx pwfx, WaveInProc dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInStop(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr hwi);
}
