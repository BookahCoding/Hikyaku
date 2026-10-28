using System.Net;

namespace Hikyaku.Tests;

/// <summary>
/// Binds a loopback <see cref="HttpListener"/> on a free port. A failed <c>Start()</c> disposes that listener, so each attempt gets a
/// fresh one (reusing one made a busy port end the scan with an ObjectDisposedException when two tests raced for the same range).
/// The scan starts at a random port in the range so parallel tests rarely contend at all.
/// </summary>
internal static class TestLoopback
{
    public static HttpListener Start(int firstPort, int lastPortExclusive, out int port)
    {
        var count = lastPortExclusive - firstPort;
        var offset = Random.Shared.Next(count);
        for (var i = 0; i < count; i++)
        {
            var p = firstPort + (offset + i) % count;
            var listener = new HttpListener();
            try
            {
                listener.Prefixes.Add($"http://127.0.0.1:{p}/");
                listener.Start();
                port = p;
                return listener;
            }
            catch (HttpListenerException)
            {
                listener.Close();          // busy: try the next port with a new listener
            }
        }
        throw new InvalidOperationException($"No free loopback port in {firstPort}-{lastPortExclusive - 1}.");
    }
}
