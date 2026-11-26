using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AirSend.Core;

/// <summary>Minimal mDNS/DNS-SD browser for _raop._tcp (no external dependencies).</summary>
public static class MdnsBrowser
{
    static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    sealed record Rec(string Name, int Type, byte[] Raw, int RdOffset, int RdLength);

    public static async Task<List<SpeakerInfo>> BrowseAsync(string service = "_raop._tcp.local", int timeoutMs = 3000, CancellationToken ct = default)
    {
        var sockets = new List<Socket>();
        foreach (var addr in LocalIPv4Addresses())
        {
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Bind(new IPEndPoint(addr, 0));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                sockets.Add(s);
            }
            catch (Exception ex) { Log.Debug($"mDNS: cannot use {addr}: {ex.Message}"); }
        }
        // Also listen on 5353 for multicast answers when the OS allows sharing the port.
        Socket? shared = null;
        try
        {
            shared = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            shared.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            shared.Bind(new IPEndPoint(IPAddress.Any, 5353));
            foreach (var addr in LocalIPv4Addresses())
                try { shared.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group.Address, addr)); } catch { }
        }
        catch { shared?.Dispose(); shared = null; }

        var records = new List<Rec>();
        var recLock = new object();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receivers = sockets.Concat(shared != null ? new[] { shared } : Array.Empty<Socket>())
            .Select(s => Task.Run(async () =>
            {
                var buf = new byte[9000];
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var r = await s.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
                        var pkt = buf.AsSpan(0, r.ReceivedBytes).ToArray();
                        var parsed = Parse(pkt);
                        lock (recLock) records.AddRange(parsed);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch { await Task.Delay(10); }
                }
            })).ToList();

        var start = DateTime.UtcNow;
        int round = 0;
        while ((DateTime.UtcNow - start).TotalMilliseconds < timeoutMs && !ct.IsCancellationRequested)
        {
            var questions = new List<(string, int)> { (service, 12) };
            if (round > 0)
            {
                List<Rec> snapshot; lock (recLock) snapshot = records.ToList();
                foreach (var inst in Instances(snapshot, service))
                {
                    if (!snapshot.Any(r => r.Type == 33 && Eq(r.Name, inst))) questions.Add((inst, 33));
                    if (!snapshot.Any(r => r.Type == 16 && Eq(r.Name, inst))) questions.Add((inst, 16));
                }
                foreach (var srv in snapshot.Where(r => r.Type == 33))
                {
                    var (_, target) = ReadSrv(srv);
                    if (!snapshot.Any(r => r.Type == 1 && Eq(r.Name, target))) questions.Add((target, 1));
                }
            }
            var q = BuildQuery(questions);
            foreach (var s in sockets) { try { s.SendTo(q, Group); } catch { } }
            round++;
            try { await Task.Delay(round == 1 ? 400 : 900, ct); } catch { break; }
        }
        cts.Cancel();
        foreach (var s in sockets) s.Dispose();
        shared?.Dispose();
        try { await Task.WhenAll(receivers); } catch { }

        List<Rec> all; lock (recLock) all = records.ToList();
        var result = new List<SpeakerInfo>();
        foreach (var inst in Instances(all, service))
        {
            var srv = all.LastOrDefault(r => r.Type == 33 && Eq(r.Name, inst));
            if (srv == null) continue;
            var (port, target) = ReadSrv(srv);
            var a = all.LastOrDefault(r => r.Type == 1 && Eq(r.Name, target) && r.RdLength == 4);
            if (a == null) continue;
            var ip = new IPAddress(a.Raw.AsSpan(a.RdOffset, 4));
            var info = new SpeakerInfo { Host = ip.ToString(), Port = port };
            var txt = all.LastOrDefault(r => r.Type == 16 && Eq(r.Name, inst));
            if (txt != null) info.Txt = ReadTxt(txt);
            var label = inst.EndsWith("." + service, StringComparison.OrdinalIgnoreCase) ? inst[..^(service.Length + 1)] : inst;
            int at = label.IndexOf('@');
            info.Name = at >= 0 ? label[(at + 1)..] : label;
            if (!result.Any(x => x.Key == info.Key)) result.Add(info);
        }
        Log.Info($"mDNS: found {result.Count} AirPlay speaker(s): {string.Join(", ", result)}");
        return result.OrderBy(r => r.Name).ToList();
    }

    static bool Eq(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    static IEnumerable<string> Instances(List<Rec> recs, string service) =>
        recs.Where(r => r.Type == 12 && Eq(r.Name, service)).Select(r => ReadName(r.Raw, r.RdOffset, out _)).Distinct(StringComparer.OrdinalIgnoreCase);

    static (int port, string target) ReadSrv(Rec r)
    {
        int port = (r.Raw[r.RdOffset + 4] << 8) | r.Raw[r.RdOffset + 5];
        return (port, ReadName(r.Raw, r.RdOffset + 6, out _));
    }

    static Dictionary<string, string> ReadTxt(Rec r)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int p = r.RdOffset, end = r.RdOffset + r.RdLength;
        while (p < end)
        {
            int len = r.Raw[p++];
            if (p + len > end) break;
            var s = Encoding.UTF8.GetString(r.Raw, p, len);
            p += len;
            int eq = s.IndexOf('=');
            if (eq > 0) d[s[..eq]] = s[(eq + 1)..]; else if (s.Length > 0) d[s] = "";
        }
        return d;
    }

    static byte[] BuildQuery(List<(string name, int type)> qs)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0, 0, 0, 0, (byte)(qs.Count >> 8), (byte)qs.Count, 0, 0, 0, 0, 0, 0 });
        foreach (var (name, type) in qs)
        {
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var b = Encoding.UTF8.GetBytes(label);
                ms.WriteByte((byte)b.Length); ms.Write(b);
            }
            ms.WriteByte(0);
            ms.Write(new byte[] { (byte)(type >> 8), (byte)type, 0x00, 0x01 }); // class IN (multicast answers)
        }
        return ms.ToArray();
    }

    static List<Rec> Parse(byte[] p)
    {
        var list = new List<Rec>();
        try
        {
            if (p.Length < 12) return list;
            if ((p[2] & 0x80) == 0) return list; // not a response
            int qd = (p[4] << 8) | p[5];
            int rrCount = ((p[6] << 8) | p[7]) + ((p[8] << 8) | p[9]) + ((p[10] << 8) | p[11]);
            int pos = 12;
            for (int i = 0; i < qd; i++) { ReadName(p, pos, out pos); pos += 4; }
            for (int i = 0; i < rrCount && pos < p.Length; i++)
            {
                string name = ReadName(p, pos, out pos);
                int type = (p[pos] << 8) | p[pos + 1];
                int rdlen = (p[pos + 8] << 8) | p[pos + 9];
                pos += 10;
                if (pos + rdlen > p.Length) break;
                list.Add(new Rec(name, type, p, pos, rdlen));
                pos += rdlen;
            }
        }
        catch { }
        return list;
    }

    static string ReadName(byte[] p, int pos, out int next)
    {
        var labels = new List<string>();
        next = -1;
        int guard = 0;
        while (guard++ < 128)
        {
            int len = p[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                int ptr = ((len & 0x3F) << 8) | p[pos + 1];
                if (next < 0) next = pos + 2;
                pos = ptr;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(p, pos + 1, len));
            pos += 1 + len;
        }
        if (next < 0) next = pos;
        return string.Join(".", labels);
    }

    public static List<IPAddress> LocalIPv4Addresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(ua.Address);
            }
        }
        catch { }
        return list;
    }

    /// <summary>Probe a manually entered speaker address and read its name/TXT if possible.</summary>
    public static async Task<SpeakerInfo?> ProbeAsync(string host, int port = 7000)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(3000);
            await tcp.ConnectAsync(host, port, cts.Token);
            var found = await BrowseAsync(timeoutMs: 2000);
            var match = found.FirstOrDefault(f => f.Host == host);
            return match ?? new SpeakerInfo { Name = host, Host = host, Port = port, Manual = true };
        }
        catch { return null; }
    }
}
