using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AirSend.Core;

/// <summary>
/// One classic AirPlay (RAOP) streaming session: RTSP handshake, then ALAC-in-RTP over UDP,
/// with sync packets, NTP timing replies and packet retransmission.
/// </summary>
public sealed class RaopSession : IDisposable
{
    public const int SampleRate = 44100;
    const int RingSize = 1024;

    // A valid (arbitrary) Curve25519 public key for POST /auth-setup. The reply is not used.
    static readonly byte[] AuthSetupKey =
    {
        0x51, 0x50, 0xdb, 0x2e, 0x5f, 0x8d, 0x5e, 0xd7, 0xc7, 0x92, 0x3e, 0xdd, 0x79, 0x7e, 0xb9, 0x9b,
        0x1f, 0x54, 0x87, 0xdc, 0x7e, 0x5a, 0xe2, 0xd9, 0x2f, 0xc4, 0x79, 0x9a, 0x9a, 0x66, 0x78, 0x63
    };

    readonly SpeakerInfo _speaker;
    readonly int _latencyFrames;
    readonly RtspConnection _rtsp = new();
    readonly Random _rng = new();
    string _url = "";
    string? _sessionId;

    Socket? _audioSock, _controlSock, _timingSock;
    IPEndPoint? _audioEp, _controlEp, _timingEp;
    volatile bool _disposed;
    Thread? _controlThread, _timingThread;

    ushort _seq;
    uint _ssrc;
    public uint StartTimestamp { get; private set; }
    bool _firstPacket = true, _firstSync = true;
    readonly byte[][] _ring = new byte[RingSize][];
    readonly int[] _ringLen = new int[RingSize];
    readonly ushort[] _ringSeq = new ushort[RingSize];
    readonly object _ringLock = new();
    readonly byte[] _alacBuf = new byte[AlacRawEncoder.MaxSize(AlacRawEncoder.FramesPerPacket)];

    public long PacketsSent { get; private set; }
    public long PacketsResent { get; private set; }
    public int SpeakerReportedLatency { get; private set; }

    /// <summary>Raised once if the speaker hangs up (null = clean close) or the connection fails.</summary>
    public event Action<Exception?>? Ended;
    volatile bool _ended;

    public RaopSession(SpeakerInfo speaker, int latencyFrames)
    {
        _speaker = speaker;
        _latencyFrames = latencyFrames;
        for (int i = 0; i < RingSize; i++) _ring[i] = new byte[12 + _alacBuf.Length];
        _rtsp.Disconnected += ex => RaiseEnded(ex);
    }

    void RaiseEnded(Exception? ex)
    {
        if (_ended || _disposed) return;
        _ended = true;
        try { Ended?.Invoke(ex); } catch { }
    }

    public bool HasEnded => _ended;

    static string Hex(int bytes, Random r) { var b = new byte[bytes]; r.NextBytes(b); return Convert.ToHexString(b); }

    public async Task ConnectAsync(CancellationToken ct)
    {
        Log.Info($"Connecting to {_speaker.Name} at {_speaker.Host}:{_speaker.Port} (et={string.Join(",", _speaker.EncryptionTypes)}, model={_speaker.Model})");
        await _rtsp.ConnectAsync(_speaker.Host, _speaker.Port, ct);
        var local = _rtsp.LocalAddress;
        var remote = _rtsp.RemoteAddress;

        string clientInstance = Hex(8, _rng);
        _rtsp.CommonHeaders["User-Agent"] = "iTunes/11.0.4 (Windows; N)";
        _rtsp.CommonHeaders["Client-Instance"] = clientInstance;
        _rtsp.CommonHeaders["DACP-ID"] = clientInstance;
        _rtsp.CommonHeaders["Active-Remote"] = ((uint)_rng.Next()).ToString();
        _url = $"rtsp://{local}/{(uint)_rng.Next()}";

        bool didAuth = false;
        if (_speaker.WantsAuthSetup)
        {
            await AuthSetupAsync();
            didAuth = true;
        }

        // ANNOUNCE
        uint sessionNum = (uint)_rng.Next();
        string sdp =
            "v=0\r\n" +
            $"o=iTunes {sessionNum} 0 IN IP4 {local}\r\n" +
            "s=iTunes\r\n" +
            $"c=IN IP4 {remote}\r\n" +
            "t=0 0\r\n" +
            "m=audio 0 RTP/AVP 96\r\n" +
            "a=rtpmap:96 AppleLossless\r\n" +
            $"a=fmtp:96 {AlacRawEncoder.FramesPerPacket} 0 16 40 10 14 2 255 0 0 {SampleRate}\r\n";
        var resp = await _rtsp.RequestAsync("ANNOUNCE", _url, null, Encoding.ASCII.GetBytes(sdp), "application/sdp");
        if ((resp.Status == 403 || resp.Status == 401 || resp.Status == 400) && !didAuth)
        {
            Log.Info($"ANNOUNCE refused ({resp}); retrying with auth-setup");
            await AuthSetupAsync();
            resp = await _rtsp.RequestAsync("ANNOUNCE", _url, null, Encoding.ASCII.GetBytes(sdp), "application/sdp");
        }
        if (resp.Status == 401) throw new RaopException("The speaker asks for an AirPlay password, which isn't supported yet. Turn off the AirPlay password in the Sonos app.");
        if (resp.Status == 453) throw new RaopException("The speaker is busy (another device is playing to it).");
        if (resp.Status != 200) throw new RaopException($"Speaker refused the stream (ANNOUNCE: {resp}).");

        // UDP sockets
        _audioSock = NewUdp(local);
        _controlSock = NewUdp(local);
        _timingSock = NewUdp(local);
        int controlPort = ((IPEndPoint)_controlSock.LocalEndPoint!).Port;
        int timingPort = ((IPEndPoint)_timingSock.LocalEndPoint!).Port;
        _timingThread = new Thread(TimingLoop) { IsBackground = true, Name = "RAOP timing" };
        _timingThread.Start();
        _controlThread = new Thread(ControlLoop) { IsBackground = true, Name = "RAOP control" };
        _controlThread.Start();

        // SETUP
        resp = await _rtsp.RequestAsync("SETUP", _url, new Dictionary<string, string>
        {
            ["Transport"] = $"RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;control_port={controlPort};timing_port={timingPort}"
        });
        if (resp.Status != 200) throw new RaopException($"Speaker refused SETUP ({resp}).");
        _sessionId = resp.Header("Session")?.Split(';')[0].Trim();
        if (string.IsNullOrEmpty(_sessionId)) throw new RaopException("Speaker did not return a Session id.");
        var transport = ParseTransport(resp.Header("Transport") ?? "");
        int serverPort = transport.GetValueOrDefault("server_port");
        int sControl = transport.GetValueOrDefault("control_port");
        int sTiming = transport.GetValueOrDefault("timing_port");
        if (serverPort == 0) throw new RaopException("Speaker did not return an audio port.");
        _audioEp = new IPEndPoint(remote, serverPort);
        _controlEp = new IPEndPoint(remote, sControl != 0 ? sControl : serverPort + 1);
        _timingEp = sTiming != 0 ? new IPEndPoint(remote, sTiming) : null;
        _rtsp.CommonHeaders["Session"] = _sessionId;
        Log.Info($"SETUP ok: audio {serverPort}, control {sControl}, timing {sTiming}");

        // Poke the speaker's timing port once so firewalls see an outgoing flow.
        SendTimingRequest();

        // RECORD
        _seq = (ushort)_rng.Next();
        _ssrc = (uint)_rng.Next();
        StartTimestamp = (uint)_rng.Next();
        resp = await _rtsp.RequestAsync("RECORD", _url, new Dictionary<string, string>
        {
            ["Range"] = "npt=0-",
            ["RTP-Info"] = $"seq={_seq};rtptime={StartTimestamp}"
        });
        if (resp.Status != 200) throw new RaopException($"Speaker refused RECORD ({resp}).");
        if (int.TryParse(resp.Header("Audio-Latency"), out int al)) SpeakerReportedLatency = al;
        Log.Info($"Streaming session started (speaker latency {SpeakerReportedLatency} frames, our latency {_latencyFrames} frames)");
    }

    async Task AuthSetupAsync()
    {
        var body = new byte[33];
        body[0] = 0x01; // no encryption
        AuthSetupKey.CopyTo(body, 1);
        var r = await _rtsp.RequestAsync("POST", "/auth-setup", null, body, "application/octet-stream");
        if (r.Status != 200) Log.Warn($"auth-setup answered {r} (continuing anyway)");
    }

    static Socket NewUdp(IPAddress local)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(local, 0));
        s.SendBufferSize = 1 << 20;
        if (OperatingSystem.IsWindows())
        {
            // Ignore ICMP "port unreachable" errors (SIO_UDP_CONNRESET), which otherwise break ReceiveFrom.
            try { s.IOControl(-1744830452, new byte[] { 0 }, null); } catch { }
        }
        return s;
    }

    static Dictionary<string, int> ParseTransport(string t)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in t.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && int.TryParse(kv[1].Split('-')[0], out int v)) d[kv[0].Trim()] = v;
        }
        return d;
    }

    /// <summary>RTP timestamp of the packet that will be sent next.</summary>
    public uint NextTimestamp => unchecked(StartTimestamp + (uint)(PacketsSent * AlacRawEncoder.FramesPerPacket));

    /// <summary>Send one packet of 352 stereo frames (interleaved 16-bit).</summary>
    public void SendAudio(ReadOnlySpan<short> pcm)
    {
        if (_audioSock == null || _audioEp == null) return;
        int alacLen = AlacRawEncoder.Encode(pcm, AlacRawEncoder.FramesPerPacket, _alacBuf);
        int slot;
        byte[] pkt;
        lock (_ringLock)
        {
            slot = _seq % RingSize;
            pkt = _ring[slot];
            pkt[0] = 0x80;
            pkt[1] = (byte)(_firstPacket ? 0xE0 : 0x60);
            BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), _seq);
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), NextTimestamp);
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(8), _ssrc);
            _alacBuf.AsSpan(0, alacLen).CopyTo(pkt.AsSpan(12));
            _ringLen[slot] = 12 + alacLen;
            _ringSeq[slot] = _seq;
            _seq++;
        }
        _firstPacket = false;
        PacketsSent++;
        try { _audioSock.SendTo(pkt, 0, 12 + alacLen, SocketFlags.None, _audioEp); }
        catch (SocketException ex) { Log.Debug($"audio send failed: {ex.SocketErrorCode}"); }
    }

    /// <summary>Tell the speaker which RTP timestamp corresponds to "now".</summary>
    public void SendSync(uint rtpNow)
    {
        if (_controlSock == null || _controlEp == null) return;
        var p = new byte[20];
        p[0] = (byte)(_firstSync ? 0x90 : 0x80);
        p[1] = 0xD4;
        p[2] = 0x00; p[3] = 0x07;
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), unchecked(rtpNow - (uint)_latencyFrames));
        BinaryPrimitives.WriteUInt64BigEndian(p.AsSpan(8), NtpClock.NowNtp());
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), rtpNow);
        _firstSync = false;
        try { _controlSock.SendTo(p, _controlEp); } catch (SocketException ex) { Log.Debug($"sync send failed: {ex.SocketErrorCode}"); }
    }

    public void SendTimingRequest()
    {
        if (_timingSock == null || _timingEp == null) return;
        var p = new byte[32];
        p[0] = 0x80; p[1] = 0xD2; p[2] = 0x00; p[3] = 0x07;
        BinaryPrimitives.WriteUInt64BigEndian(p.AsSpan(24), NtpClock.NowNtp());
        try { _timingSock.SendTo(p, _timingEp); } catch { }
    }

    void TimingLoop()
    {
        var buf = new byte[256];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        int logged = 0;
        while (!_disposed)
        {
            try
            {
                int n = _timingSock!.ReceiveFrom(buf, ref from);
                ulong recv = NtpClock.NowNtp();
                if (n >= 32 && (buf[1] & 0x7F) == 0x52) // timing request
                {
                    var r = new byte[32];
                    r[0] = 0x80; r[1] = 0xD3; r[2] = buf[2]; r[3] = buf[3];
                    Buffer.BlockCopy(buf, 24, r, 8, 8);   // origin = their transmit time
                    BinaryPrimitives.WriteUInt64BigEndian(r.AsSpan(16), recv);
                    BinaryPrimitives.WriteUInt64BigEndian(r.AsSpan(24), NtpClock.NowNtp());
                    _timingSock.SendTo(r, from);
                    if (logged++ < 2) Log.Debug($"answered timing request from {from}");
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.TimedOut) { }
            catch (Exception) { if (_disposed) return; Thread.Sleep(50); }
        }
    }

    void ControlLoop()
    {
        var buf = new byte[256];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_disposed)
        {
            try
            {
                int n = _controlSock!.ReceiveFrom(buf, ref from);
                if (n >= 8 && (buf[1] & 0x7F) == 0x55) // retransmit request
                {
                    ushort first = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4));
                    ushort count = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6));
                    Resend(first, Math.Min(count, (ushort)RingSize));
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.TimedOut) { }
            catch (Exception) { if (_disposed) return; Thread.Sleep(50); }
        }
    }

    void Resend(ushort first, int count)
    {
        var outBuf = new byte[4 + 12 + _alacBuf.Length];
        for (int i = 0; i < count; i++)
        {
            ushort seq = (ushort)(first + i);
            int slot = seq % RingSize;
            int len;
            lock (_ringLock)
            {
                if (_ringSeq[slot] != seq || _ringLen[slot] == 0) continue;
                len = _ringLen[slot];
                outBuf[0] = 0x80; outBuf[1] = 0xD6;
                BinaryPrimitives.WriteUInt16BigEndian(outBuf.AsSpan(2), 1);
                Buffer.BlockCopy(_ring[slot], 0, outBuf, 4, len);
            }
            try { _controlSock!.SendTo(outBuf, 0, 4 + len, SocketFlags.None, _controlEp!); PacketsResent++; } catch { }
        }
    }

    /// <summary>volume in AirPlay dB: -30..0, or -144 for mute.</summary>
    public async Task SetVolumeAsync(double airplayDb)
    {
        var body = Encoding.ASCII.GetBytes($"volume: {airplayDb.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}\r\n");
        var r = await _rtsp.RequestAsync("SET_PARAMETER", _url, null, body, "text/parameters");
        if (r.Status != 200) Log.Warn($"Volume change refused: {r}");
    }

    public static double PercentToAirplayDb(int percent)
    {
        if (percent <= 0) return -144.0;
        return -30.0 + 30.0 * Math.Clamp(percent, 0, 100) / 100.0;
    }

    public async Task KeepAliveAsync()
    {
        var r = await _rtsp.RequestAsync("OPTIONS", "*");
        if (r.Status != 200) Log.Warn($"Keep-alive answered {r}");
    }

    public async Task TeardownAsync()
    {
        try
        {
            if (_rtsp.IsClosed || _sessionId == null) return;
            await _rtsp.RequestAsync("FLUSH", _url, new Dictionary<string, string> { ["RTP-Info"] = $"seq={_seq};rtptime={NextTimestamp}" }, timeoutMs: 2000);
            await _rtsp.RequestAsync("TEARDOWN", _url, timeoutMs: 2000);
        }
        catch (Exception ex) { Log.Debug($"teardown: {ex.Message}"); }
    }

    public void Dispose()
    {
        _disposed = true;
        _rtsp.Dispose();
        try { _audioSock?.Close(); } catch { }
        try { _controlSock?.Close(); } catch { }
        try { _timingSock?.Close(); } catch { }
    }
}

public sealed class RaopException : Exception
{
    public RaopException(string msg) : base(msg) { }
}
