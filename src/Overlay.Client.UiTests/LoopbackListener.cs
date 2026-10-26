using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Overlay.Client.UiTests;

/// <summary>One HTTP request the listener received.</summary>
public sealed record RecordedRequest(string Method, string Path, DateTime Time);

/// <summary>
/// A loopback HTTP endpoint for a session's <c>telemetry.endpoint</c>: it answers every request with
/// 204 and records method, path and time, so telemetry never leaves the machine (spec R18).
/// </summary>
public sealed class LoopbackListener : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<RecordedRequest> _requests = new();
    private readonly CancellationTokenSource _cts = new();

    public LoopbackListener()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    /// <summary>The URL seeded as <c>telemetry.endpoint</c>.</summary>
    public string Url => $"http://127.0.0.1:{Port}";

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch { return; } // stopped
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                var head = await ReadHead(stream).ConfigureAwait(false);
                var lines = head.Split("\r\n");
                var parts = lines[0].Split(' ');
                if (parts.Length >= 2)
                    lock (_requests) _requests.Add(new RecordedRequest(parts[0], parts[1], DateTime.Now));

                int length = 0;
                foreach (var line in lines)
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(line["Content-Length:".Length..].Trim(), out length);
                var buffer = new byte[4096];
                while (length > 0)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, length))).ConfigureAwait(false);
                    if (n <= 0) break;
                    length -= n;
                }

                var reply = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                await stream.WriteAsync(reply).ConfigureAwait(false);
            }
            catch
            {
                // A client that disconnects early is not a test failure.
            }
        }
    }

    private static async Task<string> ReadHead(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 64 * 1024)
        {
            int n = await stream.ReadAsync(one).ConfigureAwait(false);
            if (n <= 0) break;
            bytes.Add(one[0]);
            int c = bytes.Count;
            if (c >= 4 && bytes[c - 4] == '\r' && bytes[c - 3] == '\n' && bytes[c - 2] == '\r' && bytes[c - 1] == '\n') break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
