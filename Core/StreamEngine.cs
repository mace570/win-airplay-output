using NAudio.Dsp;

namespace AirSend.Core;

public enum EngineState { Stopped, Connecting, Streaming, Reconnecting, Error }

public sealed class EngineOptions
{
    /// <summary>Buffer on the speaker side, in milliseconds (AirPlay default is 2 s).</summary>
    public int LatencyMs { get; set; } = 2000;
    /// <summary>Local jitter buffer between capture and network, in milliseconds.</summary>
    public int LocalBufferMs { get; set; } = 300;
    public int VolumePercent { get; set; } = 50;
    public bool Dither { get; set; } = true;
}

/// <summary>
/// Pulls audio from a capture source, resamples to 44.1 kHz (with gentle drift correction)
/// and paces it out to an AirPlay speaker in real time. Reconnects on network trouble.
/// </summary>
public sealed class StreamEngine : IDisposable
{
    readonly Func<IAudioCapture> _captureFactory;
    readonly SpeakerInfo _speaker;
    readonly EngineOptions _opt;
    readonly AudioFifo _fifo = new();
    Thread? _thread;
    volatile bool _stop;
    RaopSession? _session;
    IAudioCapture? _capture;
    volatile int _volume;
    volatile bool _volumeDirty;

    public EngineState State { get; private set; } = EngineState.Stopped;
    public string StatusText { get; private set; } = "Stopped";
    public event Action<EngineState, string>? StatusChanged;
    /// <summary>Peak level (0..1) of the captured audio, updated a few times a second.</summary>
    public float Level { get; private set; }

    public StreamEngine(Func<IAudioCapture> captureFactory, SpeakerInfo speaker, EngineOptions opt)
    {
        _captureFactory = captureFactory;
        _speaker = speaker;
        _opt = opt;
        _volume = opt.VolumePercent;
    }

    public SpeakerInfo Speaker => _speaker;

    void SetStatus(EngineState s, string text)
    {
        State = s; StatusText = text;
        Log.Info($"[{s}] {text}");
        try { StatusChanged?.Invoke(s, text); } catch { }
    }

    public void Start()
    {
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "AirSend engine", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(5000);
        _thread = null;
    }

    public void SetVolume(int percent) { _volume = percent; _volumeDirty = true; }

    void EnsureCapture()
    {
        if (_capture != null && _capture.IsAlive) return;
        if (_capture != null) { Log.Warn("Audio capture stopped; restarting it"); try { _capture.Dispose(); } catch { } _capture = null; }
        var c = _captureFactory();
        c.Start(_fifo);
        _capture = c;
        Log.Info($"Capturing: {c.Description}");
    }

    void Run()
    {
        int attempt = 0;
        while (!_stop)
        {
            try
            {
                EnsureCapture();
                SetStatus(attempt == 0 ? EngineState.Connecting : EngineState.Reconnecting, $"Connecting to {_speaker.Name}…");
                RunSession();
                if (_stop) break;
            }
            catch (SpeakerClosedException)
            {
                SetStatus(EngineState.Stopped, $"{_speaker.Name} ended the stream (another source may have taken over).");
                Cleanup();
                return;
            }
            catch (RaopException ex) when (ex.Message.Contains("password"))
            {
                SetStatus(EngineState.Error, ex.Message);
                Cleanup();
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Stream error", ex);
                if (_stop) break;
                attempt++;
                if (attempt >= 2 && ex is not RaopException) TryRediscover();
                int wait = Math.Min(30, 2 + attempt * 2);
                SetStatus(EngineState.Reconnecting, $"{Short(ex)} Retrying in {wait} s…");
                for (int i = 0; i < wait * 10 && !_stop; i++) Thread.Sleep(100);
                continue;
            }
        }
        Cleanup();
        SetStatus(EngineState.Stopped, "Stopped");
    }

    /// <summary>The speaker may have a new IP address (DHCP); look it up again by name.</summary>
    void TryRediscover()
    {
        if (_speaker.Manual) return;
        try
        {
            var found = MdnsBrowser.BrowseAsync(timeoutMs: 2500).GetAwaiter().GetResult();
            var m = found.FirstOrDefault(f => string.Equals(f.Name, _speaker.Name, StringComparison.OrdinalIgnoreCase));
            if (m != null && (m.Host != _speaker.Host || m.Port != _speaker.Port))
            {
                Log.Info($"{_speaker.Name} moved from {_speaker.Host}:{_speaker.Port} to {m.Host}:{m.Port}");
                _speaker.Host = m.Host; _speaker.Port = m.Port; _speaker.Txt = m.Txt;
                SpeakerMoved?.Invoke(_speaker);
            }
        }
        catch (Exception e) { Log.Debug($"rediscovery failed: {e.Message}"); }
    }

    public event Action<SpeakerInfo>? SpeakerMoved;

    static string Short(Exception ex) => ex switch
    {
        RaopException r => r.Message,
        TimeoutException => "The speaker did not answer.",
        System.Net.Sockets.SocketException s => $"Network error ({s.SocketErrorCode}).",
        IOException => "Connection to the speaker was lost.",
        _ => ex.Message
    };

    void Cleanup()
    {
        try { _capture?.Dispose(); } catch { }
        _capture = null;
    }

    sealed class SpeakerClosedException : Exception { }

    void RunSession()
    {
        const int Frames = AlacRawEncoder.FramesPerPacket;
        const double PacketSeconds = Frames / (double)RaopSession.SampleRate;
        int latencyFrames = (int)(_opt.LatencyMs / 1000.0 * RaopSession.SampleRate);

        using var session = new RaopSession(_speaker, latencyFrames);
        Exception? ended = null; bool endedFlag = false;
        session.Ended += ex => { ended = ex; endedFlag = true; };
        session.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        _session = session;
        session.SetVolumeAsync(RaopSession.PercentToAirplayDb(_volume)).GetAwaiter().GetResult();
        _volumeDirty = false;

        var resampler = new WdlResampler();
        resampler.SetMode(true, 0, true, 64);
        resampler.SetFilterParms();
        resampler.SetFeedMode(false);
        int inRate = _fifo.SampleRate;
        resampler.SetRates(inRate, RaopSession.SampleRate);

        var outF = new float[Frames * 2];
        var pcm = new short[Frames * 2];
        var rnd = new Random();
        bool primed = false;
        double smoothLevel = 0;
        int underruns = 0;

        _fifo.Skip(_fifo.FramesAvailable); // start fresh
        double t0 = NtpClock.Now;
        long k = 0;
        double nextSync = t0, nextKeepAlive = t0 + 15, nextStats = t0 + 60, nextLevel = t0;
        Task? keepAlive = null;
        SetStatus(EngineState.Streaming, $"Streaming to {_speaker.Name}");
        session.SendSync(session.StartTimestamp);

        while (!_stop)
        {
            if (endedFlag)
            {
                if (ended == null) throw new SpeakerClosedException();
                throw new IOException("Connection lost", ended);
            }
            EnsureCapture();
            double now = NtpClock.Now;

            if (now - (t0 + k * PacketSeconds) > 1.0)
                throw new IOException("Audio clock jumped (PC was busy or asleep)");

            while (t0 + k * PacketSeconds <= now)
            {
                // ---- produce one packet ----
                if (_fifo.SampleRate != inRate)
                {
                    inRate = _fifo.SampleRate;
                    resampler.Reset();
                    primed = false;
                }
                int targetFrames = Math.Max(inRate * _opt.LocalBufferMs / 1000, 2048);
                int avail = _fifo.FramesAvailable;
                smoothLevel = smoothLevel * 0.98 + avail * 0.02;
                if (!primed && avail >= targetFrames) { primed = true; smoothLevel = avail; resampler.Reset(); }
                if (avail > targetFrames * 4 + inRate) { _fifo.Skip(avail - targetFrames); smoothLevel = targetFrames; Log.Debug("buffer overflow, skipped audio"); }

                bool haveAudio = false;
                if (primed)
                {
                    double errSec = (smoothLevel - targetFrames) / inRate;
                    double corr = Math.Clamp(errSec * 0.01, -0.002, 0.002);
                    resampler.SetRates(inRate * (1 + corr), RaopSession.SampleRate);
                    int needed = resampler.ResamplePrepare(Frames, 2, out float[] inBuf, out int inOff);
                    if (_fifo.FramesAvailable >= needed)
                    {
                        int got = _fifo.Read(inBuf.AsSpan(inOff, needed * 2));
                        if (got < needed) Array.Clear(inBuf, inOff + got * 2, (needed - got) * 2);
                        int outGot = resampler.ResampleOut(outF, 0, needed, Frames, 2);
                        if (outGot < Frames) Array.Clear(outF, outGot * 2, (Frames - outGot) * 2);
                        haveAudio = true;
                    }
                    else
                    {
                        primed = false; underruns++;
                    }
                }
                if (haveAudio)
                {
                    for (int i = 0; i < outF.Length; i++)
                    {
                        double v = outF[i] * 32767.0;
                        if (_opt.Dither) v += (rnd.NextDouble() - rnd.NextDouble());
                        pcm[i] = (short)Math.Clamp(Math.Round(v), -32768, 32767);
                    }
                }
                else Array.Clear(pcm);
                session.SendAudio(pcm);
                k++;
            }

            if (now >= nextSync)
            {
                uint rtpNow = unchecked(session.StartTimestamp + (uint)((now - t0) * RaopSession.SampleRate));
                session.SendSync(rtpNow);
                nextSync += 1.0;
                if (k % 3 == 0) session.SendTimingRequest();
            }
            if (_volumeDirty && (keepAlive == null || keepAlive.IsCompleted))
            {
                _volumeDirty = false;
                keepAlive = session.SetVolumeAsync(RaopSession.PercentToAirplayDb(_volume));
            }
            if (now >= nextKeepAlive && (keepAlive == null || keepAlive.IsCompleted))
            {
                nextKeepAlive = now + 15;
                keepAlive = session.KeepAliveAsync();
            }
            if (keepAlive != null && keepAlive.IsFaulted)
            {
                var ex = keepAlive.Exception!.GetBaseException();
                keepAlive = null;
                if (endedFlag && ended == null) throw new SpeakerClosedException();
                throw new IOException("Speaker stopped answering", ex);
            }
            if (now >= nextLevel) { Level = _fifo.TakePeak(); nextLevel = now + 0.2; }
            if (now >= nextStats)
            {
                nextStats = now + 60;
                Log.Info($"stats: sent {session.PacketsSent} packets, resent {session.PacketsResent}, underruns {underruns}, local buffer {smoothLevel / inRate * 1000:0} ms @ {inRate} Hz");
            }
            Thread.Sleep(2);
        }

        Log.Info("Stopping stream");
        session.TeardownAsync().Wait(3000);
        _session = null;
    }

    public void Dispose() => Stop();
}
