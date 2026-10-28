using System.Diagnostics;
using System.Runtime.InteropServices;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// The microphone and speaker of one voice chat, through Windows' classic wave API (winmm), which every PC has: the
/// microphone runs continuously as 16 kHz mono 16-bit, handed out in 20 ms frames; speech is raw 16-bit PCM at whatever rate
/// the gateway's voice uses, queued as it arrives. One worker thread waits on both devices' event callbacks (the safe winmm
/// pattern: no device calls from inside a callback). Not unit-testable (hardware); the voice chat's decisions live in Core.
/// </summary>
public sealed class WinMmVoiceIo : IVoiceChatAudio
{
    private const int MicRate = 16_000;
    private const int FrameSamples = 320;                 // 20 ms
    private const int MicBuffers = 8;
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint CallbackEvent = 0x00050000;
    private const int WhdrDone = 0x1;

    private readonly AutoResetEvent _micEvent = new(false), _outEvent = new(false), _wake = new(false);
    private readonly object _outGate = new();
    private readonly List<IntPtr> _micHeaders = new();
    private readonly List<IntPtr> _queued = new();
    private readonly List<short> _micCarry = new();
    private readonly Thread _worker;
    private IntPtr _waveIn, _waveOut;
    private (int Rate, int Channels) _outFormat;
    private long _micSamples;
    private int _nextMic;
    private bool _ending, _playing;
    private volatile bool _disposed;

    /// <summary>A 20 ms microphone frame and the time it ends, in ms since the chat began (from the sample count).</summary>
    public event Action<short[], long>? MicFrame;
    public event Action<bool>? PlayingChanged;
    /// <summary>Everything queued after <see cref="EndSpeech"/> has been played.</summary>
    public event Action? SpeechDrained;

    private WinMmVoiceIo()
    {
        _worker = new Thread(Work) { IsBackground = true, Name = "Hikyaku voice chat audio" };
    }

    /// <summary>Opens the microphone; a problem in plain words when it cannot (none, in use, or blocked in Windows' privacy settings).</summary>
    public static (WinMmVoiceIo? Io, string? Problem) Open()
    {
        var io = new WinMmVoiceIo();
        var fmt = Format(MicRate, 1);
        var mr = waveInOpen(out io._waveIn, WaveMapper, ref fmt, io._micEvent.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent);
        if (mr != 0)
        {
            io.Dispose();
            return (null, "Hikyaku could not open the microphone. Check that one is connected and that Windows lets apps use it (Settings, Privacy, Microphone).");
        }
        for (var i = 0; i < MicBuffers; i++)
        {
            var header = NewHeader(FrameSamples * 2 * 2);  // 40 ms per buffer
            io._micHeaders.Add(header);
            waveInPrepareHeader(io._waveIn, header, HeaderSize);
            waveInAddBuffer(io._waveIn, header, HeaderSize);
        }
        waveInStart(io._waveIn);
        io._worker.Start();
        return (io, null);
    }

    public bool IsPlaying { get { lock (_outGate) return _playing; } }

    /// <summary>Starts a reply's speech at the gateway's rate (reopens the speaker only when the format changes).</summary>
    public void BeginSpeech(int sampleRate, int channels)
    {
        lock (_outGate)
        {
            _ending = false;
            if (_waveOut != IntPtr.Zero && _outFormat == (sampleRate, channels)) return;
            CloseOutLocked();
            var fmt = Format(sampleRate, channels);
            if (waveOutOpen(out _waveOut, WaveMapper, ref fmt, _outEvent.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent) != 0)
                _waveOut = IntPtr.Zero;
            _outFormat = (sampleRate, channels);
        }
    }

    public void QueuePcm(byte[] pcm16le)
    {
        if (pcm16le.Length == 0) return;
        bool started;
        lock (_outGate)
        {
            if (_waveOut == IntPtr.Zero) return;
            var header = NewHeader(pcm16le.Length);
            Marshal.Copy(pcm16le, 0, Marshal.ReadIntPtr(header), pcm16le.Length);
            waveOutPrepareHeader(_waveOut, header, HeaderSize);
            waveOutWrite(_waveOut, header, HeaderSize);
            _queued.Add(header);
            started = !_playing;
            _playing = true;
        }
        if (started) PlayingChanged?.Invoke(true);
    }

    /// <summary>The reply is complete: <see cref="SpeechDrained"/> follows once what is queued has played.</summary>
    public void EndSpeech()
    {
        lock (_outGate) _ending = true;
        _wake.Set();
    }

    /// <summary>Stops at once (someone talked over it); no <see cref="SpeechDrained"/> follows.</summary>
    public void StopSpeech()
    {
        bool wasPlaying;
        lock (_outGate)
        {
            _ending = false;
            if (_waveOut != IntPtr.Zero) waveOutReset(_waveOut);
            FreeDoneLocked(all: true);
            wasPlaying = _playing;
            _playing = false;
        }
        if (wasPlaying) PlayingChanged?.Invoke(false);
    }

    private void Work()
    {
        var handles = new WaitHandle[] { _micEvent, _outEvent, _wake };
        while (!_disposed)
        {
            WaitHandle.WaitAny(handles, 100);
            if (_disposed) break;
            ReadMic();
            CheckSpeech();
        }
    }

    private void ReadMic()
    {
        // In the order the driver filled them: the next one due first, stopping at the first not yet done.
        var done = _micHeaders.Select(h => (Marshal.ReadInt32(h, FlagsOffset) & WhdrDone) != 0).ToArray();
        foreach (var index in MicBufferRing.TakeDone(ref _nextMic, done))
        {
            var header = _micHeaders[index];
            var bytes = Marshal.ReadInt32(header, RecordedOffset);
            if (bytes > 0)
            {
                var samples = new short[bytes / 2];
                Marshal.Copy(Marshal.ReadIntPtr(header), samples, 0, samples.Length);
                _micCarry.AddRange(samples);
            }
            if (_disposed) return;
            Marshal.WriteInt32(header, FlagsOffset, Marshal.ReadInt32(header, FlagsOffset) & ~WhdrDone);
            waveInAddBuffer(_waveIn, header, HeaderSize);
        }
        while (_micCarry.Count >= FrameSamples)
        {
            var frame = _micCarry.GetRange(0, FrameSamples).ToArray();
            _micCarry.RemoveRange(0, FrameSamples);
            _micSamples += FrameSamples;
            try { MicFrame?.Invoke(frame, _micSamples * 1000 / MicRate); }
            catch (Exception ex) { Debug.WriteLine("voice frame handler failed: " + ex.Message); }
        }
    }

    private void CheckSpeech()
    {
        bool drained = false, stopped = false;
        lock (_outGate)
        {
            FreeDoneLocked(all: false);
            if (_playing && _queued.Count == 0 && _ending)
            {
                _playing = false;
                _ending = false;
                drained = stopped = true;
            }
        }
        if (stopped) PlayingChanged?.Invoke(false);
        if (drained) SpeechDrained?.Invoke();
    }

    private void FreeDoneLocked(bool all)
    {
        for (var i = _queued.Count - 1; i >= 0; i--)
        {
            var header = _queued[i];
            if (!all && (Marshal.ReadInt32(header, FlagsOffset) & WhdrDone) == 0) continue;
            if (_waveOut != IntPtr.Zero) waveOutUnprepareHeader(_waveOut, header, HeaderSize);
            FreeHeader(header);
            _queued.RemoveAt(i);
        }
    }

    private void CloseOutLocked()
    {
        if (_waveOut == IntPtr.Zero) return;
        waveOutReset(_waveOut);
        FreeDoneLocked(all: true);
        waveOutClose(_waveOut);
        _waveOut = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        if (_worker.IsAlive) _worker.Join(1000);
        if (_waveIn != IntPtr.Zero)
        {
            waveInReset(_waveIn);
            foreach (var header in _micHeaders) { waveInUnprepareHeader(_waveIn, header, HeaderSize); FreeHeader(header); }
            waveInClose(_waveIn);
            _waveIn = IntPtr.Zero;
        }
        lock (_outGate) CloseOutLocked();
        _micEvent.Dispose();
        _outEvent.Dispose();
        _wake.Dispose();
    }

    // ---- winmm ----

    private static WaveFormatEx Format(int rate, int channels) => new()
    {
        wFormatTag = 1, nChannels = (ushort)channels, nSamplesPerSec = (uint)rate, wBitsPerSample = 16,
        nBlockAlign = (ushort)(channels * 2), nAvgBytesPerSec = (uint)(rate * channels * 2), cbSize = 0
    };

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

    private static readonly int HeaderSize = Marshal.SizeOf<WaveHdr>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwFlags));
    private static readonly int RecordedOffset = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwBytesRecorded));

    private static IntPtr NewHeader(int bytes)
    {
        var header = Marshal.AllocHGlobal(HeaderSize);
        Marshal.StructureToPtr(new WaveHdr { lpData = Marshal.AllocHGlobal(bytes), dwBufferLength = (uint)bytes }, header, false);
        return header;
    }

    private static void FreeHeader(IntPtr header)
    {
        Marshal.FreeHGlobal(Marshal.ReadIntPtr(header));
        Marshal.FreeHGlobal(header);
    }

    [DllImport("winmm.dll")] private static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WaveFormatEx pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveInStart(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInReset(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveInClose(IntPtr hwi);
    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WaveFormatEx pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
}

/// <summary>
/// The microphone's buffers are queued round-robin; after a stall several are done at once, and they must be read from the one
/// due next, in turn, so the sound stays in order (0.6.0 review: reading them in list order scrambled a cut-in's first words).
/// </summary>
public static class MicBufferRing
{
    public static int[] TakeDone(ref int next, bool[] done)
    {
        var taken = new List<int>();
        while (taken.Count < done.Length && done[next])
        {
            taken.Add(next);
            next = (next + 1) % done.Length;
        }
        return taken.ToArray();
    }
}
