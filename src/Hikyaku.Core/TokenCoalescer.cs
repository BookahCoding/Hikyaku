using System.Text;

namespace Hikyaku.Core;

/// <summary>
/// Batches streamed token deltas so the UI updates the active message at a bounded rate
/// instead of once per token. Completed messages are never touched.
/// </summary>
public sealed class TokenCoalescer(Action<string> flush, TimeSpan interval, Func<DateTime>? clock = null)
{
    private readonly StringBuilder _buf = new();
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private DateTime _last = DateTime.MinValue;
    public int FlushCount { get; private set; }

    public void Append(string delta)
    {
        _buf.Append(delta);
        if (_now() - _last >= interval) Flush();
    }

    public void Flush()
    {
        if (_buf.Length == 0) return;
        var s = _buf.ToString();
        _buf.Clear();
        _last = _now();
        FlushCount++;
        flush(s);
    }
}
