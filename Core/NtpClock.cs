using System.Diagnostics;

namespace AirSend.Core;

/// <summary>Monotonic clock, also expressed as NTP (seconds since 1900) for AirPlay timing.</summary>
public static class NtpClock
{
    static readonly long BaseTicks = Stopwatch.GetTimestamp();
    static readonly double BaseNtpSeconds =
        (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds + 2208988800.0;

    /// <summary>Seconds since process start (monotonic).</summary>
    public static double Now => (Stopwatch.GetTimestamp() - BaseTicks) / (double)Stopwatch.Frequency;

    public static ulong ToNtp(double monotonicSeconds)
    {
        double t = BaseNtpSeconds + monotonicSeconds;
        double secs = Math.Floor(t);
        uint frac = (uint)Math.Min(uint.MaxValue, (t - secs) * 4294967296.0);
        return ((ulong)(uint)secs << 32) | frac;
    }

    public static ulong NowNtp() => ToNtp(Now);
}
