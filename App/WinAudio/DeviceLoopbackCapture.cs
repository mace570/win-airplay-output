using AirSend.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AirSend.WinAudio;

/// <summary>Captures everything played on an output device (WASAPI loopback). Self-healing.</summary>
public sealed class DeviceLoopbackCapture : IAudioCapture
{
    readonly string? _deviceId; // null = follow the default output device
    Thread? _thread;
    volatile bool _stop;
    public DeviceLoopbackCapture(string? deviceId) { _deviceId = deviceId; }
    public string Description { get; private set; } = "System audio";
    public bool IsAlive => !_stop;

    public void Start(AudioFifo fifo)
    {
        _thread = new Thread(() => Loop(fifo)) { IsBackground = true, Name = "Loopback capture", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public static List<(string id, string name)> ListDevices()
    {
        var list = new List<(string, string)>();
        using var en = new MMDeviceEnumerator();
        foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            list.Add((d.ID, d.FriendlyName));
        return list;
    }

    void Loop(AudioFifo fifo)
    {
        while (!_stop)
        {
            try { CaptureOnce(fifo); }
            catch (Exception ex) { Log.Error("Device capture failed", ex); }
            for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100);
        }
    }

    void CaptureOnce(AudioFifo fifo)
    {
        using var en = new MMDeviceEnumerator();
        MMDevice dev;
        if (_deviceId != null)
        {
            try { dev = en.GetDevice(_deviceId); }
            catch { Log.Warn("Chosen output device not found; using the default device"); dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
            if (dev.State != DeviceState.Active) { Log.Warn($"Output device '{dev.FriendlyName}' is not active; using the default device"); dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        }
        else dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        string devId = dev.ID;

        using var ac = dev.AudioClient;
        var fmt = ac.MixFormat;
        SampleKind kind = Kind(fmt);
        ac.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, 2_000_000, 0, fmt, Guid.Empty);
        var cc = ac.AudioCaptureClient;
        ac.Start();
        Description = $"All sound from '{dev.FriendlyName}' ({fmt.SampleRate} Hz, {fmt.Channels} ch, {kind})";
        Log.Info($"Loopback capture started: {Description}");
        var stereo = new float[fmt.SampleRate * 2];
        double nextDefaultCheck = NtpClock.Now + 2;
        try
        {
            while (!_stop)
            {
                Thread.Sleep(5);
                int packet = cc.GetNextPacketSize();
                while (packet > 0)
                {
                    IntPtr buf = cc.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    if (frames * 2 > stereo.Length) stereo = new float[frames * 2];
                    if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(stereo, 0, frames * 2);
                    else PcmConvert.ToStereo(buf, frames, fmt.Channels, kind, stereo);
                    cc.ReleaseBuffer(frames);
                    fifo.Write(stereo.AsSpan(0, frames * 2), fmt.SampleRate);
                    packet = cc.GetNextPacketSize();
                }
                if (NtpClock.Now >= nextDefaultCheck)
                {
                    nextDefaultCheck = NtpClock.Now + 2;
                    if (_deviceId == null)
                    {
                        using var d2 = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        if (d2.ID != devId) { Log.Info($"Default output changed to '{d2.FriendlyName}', switching"); break; }
                    }
                }
            }
        }
        finally
        {
            try { ac.Stop(); } catch { }
        }
    }

    internal static SampleKind Kind(WaveFormat fmt)
    {
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        if (isFloat) return SampleKind.Float32;
        return fmt.BitsPerSample switch { 16 => SampleKind.Int16, 24 => SampleKind.Int24, 32 => SampleKind.Int32, _ => throw new NotSupportedException($"Unsupported audio format {fmt}") };
    }

    public void Dispose() { _stop = true; _thread?.Join(2000); }
}
