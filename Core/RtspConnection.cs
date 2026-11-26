using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AirSend.Core;

public sealed class RtspResponse
{
    public int Status;
    public string Reason = "";
    public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body = Array.Empty<byte>();
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    public override string ToString() => $"{Status} {Reason}";
}

/// <summary>RTSP/1.0 client over TCP with a background reader so a remote hang-up is noticed immediately.</summary>
public sealed class RtspConnection : IDisposable
{
    readonly TcpClient _tcp = new() { NoDelay = true };
    NetworkStream? _stream;
    int _cseq;
    readonly SemaphoreSlim _lock = new(1, 1);
    TaskCompletionSource<RtspResponse>? _pending;
    volatile bool _closed;
    public IPAddress LocalAddress { get; private set; } = IPAddress.Any;
    public IPAddress RemoteAddress { get; private set; } = IPAddress.Any;
    public Dictionary<string, string> CommonHeaders { get; } = new();

    /// <summary>Raised once when the connection ends. Argument is null for a clean remote close.</summary>
    public event Action<Exception?>? Disconnected;

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        await _tcp.ConnectAsync(host, port, cts.Token);
        _tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        _stream = _tcp.GetStream();
        LocalAddress = ((IPEndPoint)_tcp.Client.LocalEndPoint!).Address.MapToIPv4();
        RemoteAddress = ((IPEndPoint)_tcp.Client.RemoteEndPoint!).Address.MapToIPv4();
        _ = Task.Run(ReaderLoop);
    }

    public bool IsClosed => _closed;

    public async Task<RtspResponse> RequestAsync(string method, string uri, IDictionary<string, string>? headers = null,
        byte[]? body = null, string? contentType = null, int timeoutMs = 6000)
    {
        if (_stream == null || _closed) throw new IOException("RTSP connection is closed");
        await _lock.WaitAsync();
        try
        {
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(uri).Append(" RTSP/1.0\r\n");
            sb.Append("CSeq: ").Append(++_cseq).Append("\r\n");
            foreach (var kv in CommonHeaders) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            if (headers != null) foreach (var kv in headers) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            if (body != null)
            {
                if (contentType != null) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
                sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            }
            sb.Append("\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            Log.Debug($"RTSP > {method} {uri} (CSeq {_cseq}{(body != null ? $", {body.Length} bytes" : "")})");

            var tcs = new TaskCompletionSource<RtspResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            var packet = new byte[head.Length + (body?.Length ?? 0)];
            head.CopyTo(packet, 0);
            body?.CopyTo(packet, head.Length);
            await _stream.WriteAsync(packet);
            await _stream.FlushAsync();

            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (done != tcs.Task) { _pending = null; throw new TimeoutException($"No answer to {method} within {timeoutMs} ms"); }
            var resp = await tcs.Task;
            Log.Debug($"RTSP < {resp.Status} {resp.Reason} for {method}" +
                      (resp.Headers.Count > 0 ? " [" + string.Join("; ", resp.Headers.Select(h => $"{h.Key}: {h.Value}")) + "]" : ""));
            return resp;
        }
        finally { _lock.Release(); }
    }

    async Task ReaderLoop()
    {
        Exception? error = null;
        try
        {
            var s = _stream!;
            var buf = new List<byte>(1024);
            var one = new byte[1];
            while (true)
            {
                // read header block
                buf.Clear();
                while (true)
                {
                    int n = await s.ReadAsync(one.AsMemory(0, 1));
                    if (n == 0) { error = null; goto closed; }
                    buf.Add(one[0]);
                    int c = buf.Count;
                    if (c >= 4 && buf[c - 4] == '\r' && buf[c - 3] == '\n' && buf[c - 2] == '\r' && buf[c - 1] == '\n') break;
                    if (c > 64 * 1024) throw new IOException("RTSP header too long");
                }
                var text = Encoding.ASCII.GetString(buf.ToArray());
                var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var resp = new RtspResponse();
                var first = lines[0].Split(' ', 3);
                if (first.Length >= 2 && first[0].StartsWith("RTSP/")) { int.TryParse(first[1], out resp.Status); resp.Reason = first.Length > 2 ? first[2] : ""; }
                foreach (var l in lines.Skip(1))
                {
                    int i = l.IndexOf(':');
                    if (i > 0) resp.Headers[l[..i].Trim()] = l[(i + 1)..].Trim();
                }
                if (int.TryParse(resp.Header("Content-Length"), out int len) && len > 0)
                {
                    resp.Body = new byte[len];
                    int got = 0;
                    while (got < len)
                    {
                        int n = await s.ReadAsync(resp.Body.AsMemory(got, len - got));
                        if (n == 0) goto closed;
                        got += n;
                    }
                }
                if (!first[0].StartsWith("RTSP/"))
                {
                    Log.Debug($"RTSP: ignoring request from speaker: {lines[0]}");
                    continue;
                }
                var p = _pending; _pending = null;
                p?.TrySetResult(resp);
            }
        }
        catch (Exception ex) { error = ex; }
    closed:
        if (_closed) return;
        _closed = true;
        _pending?.TrySetException(error ?? new IOException("Speaker closed the connection"));
        try { Disconnected?.Invoke(error); } catch { }
    }

    public void Dispose()
    {
        _closed = true;
        try { _tcp.Close(); } catch { }
    }
}
