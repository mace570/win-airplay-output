namespace AirSend.Core;

/// <summary>Thread-safe FIFO of interleaved stereo float frames at a given sample rate.</summary>
public sealed class AudioFifo
{
    readonly object _lock = new();
    float[] _buf = new float[2 * 48000 * 4];
    int _read, _count; // in floats
    public int SampleRate { get; private set; } = 48000;
    public long TotalWritten { get; private set; }
    public float Peak; // since last read of peak

    public void Write(ReadOnlySpan<float> stereo, int sampleRate)
    {
        lock (_lock)
        {
            if (sampleRate != SampleRate)
            {
                SampleRate = sampleRate;
                _buf = new float[2 * sampleRate * 4];
                _read = _count = 0;
            }
            int cap = _buf.Length;
            if (stereo.Length > cap) stereo = stereo[^cap..];
            int overflow = _count + stereo.Length - cap;
            if (overflow > 0) { _read = (_read + overflow) % cap; _count -= overflow; }
            int w = (_read + _count) % cap;
            int first = Math.Min(stereo.Length, cap - w);
            stereo[..first].CopyTo(_buf.AsSpan(w));
            stereo[first..].CopyTo(_buf.AsSpan(0));
            _count += stereo.Length;
            TotalWritten += stereo.Length / 2;
            foreach (var s in stereo) { var a = Math.Abs(s); if (a > Peak) Peak = a; }
        }
    }

    public int FramesAvailable { get { lock (_lock) return _count / 2; } }

    public int Read(Span<float> dest)
    {
        lock (_lock)
        {
            int n = Math.Min(dest.Length, _count) & ~1;
            int cap = _buf.Length;
            int first = Math.Min(n, cap - _read);
            _buf.AsSpan(_read, first).CopyTo(dest);
            _buf.AsSpan(0, n - first).CopyTo(dest[first..]);
            _read = (_read + n) % cap;
            _count -= n;
            return n / 2;
        }
    }

    public void Skip(int frames)
    {
        lock (_lock)
        {
            int n = Math.Min(frames * 2, _count);
            _read = (_read + n) % _buf.Length;
            _count -= n;
        }
    }

    public void Clear() { lock (_lock) { _read = _count = 0; } }

    public float TakePeak() { lock (_lock) { var p = Peak; Peak = 0; return p; } }
}

/// <summary>An audio source that pushes stereo float frames into a FIFO.</summary>
public interface IAudioCapture : IDisposable
{
    string Description { get; }
    /// <summary>Starts capturing. Throws if the source can't be opened.</summary>
    void Start(AudioFifo fifo);
    /// <summary>True while the capture is healthy; false once it has failed and must be restarted.</summary>
    bool IsAlive { get; }
    /// <summary>Optional short note for the UI (e.g. "muted on this PC").</summary>
    string? Note => null;
}

/// <summary>Test source: a sine tone generated in real time.</summary>
public sealed class ToneCapture : IAudioCapture
{
    readonly double _freq;
    readonly int _rate;
    Thread? _t; volatile bool _stop;
    public ToneCapture(double freq = 440, int rate = 48000) { _freq = freq; _rate = rate; }
    public string Description => $"Test tone {_freq} Hz";
    public bool IsAlive => !_stop;
    public void Start(AudioFifo fifo)
    {
        _t = new Thread(() =>
        {
            double phase = 0; long produced = 0; var t0 = NtpClock.Now;
            var chunk = new float[_rate / 100 * 2];
            while (!_stop)
            {
                long due = (long)((NtpClock.Now - t0) * _rate);
                while (produced + chunk.Length / 2 <= due)
                {
                    for (int i = 0; i < chunk.Length; i += 2)
                    {
                        float v = (float)(0.3 * Math.Sin(phase));
                        chunk[i] = chunk[i + 1] = v;
                        phase += 2 * Math.PI * _freq / _rate;
                    }
                    if (phase > 2 * Math.PI * 1000) phase %= 2 * Math.PI;
                    fifo.Write(chunk, _rate);
                    produced += chunk.Length / 2;
                }
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        _t.Start();
    }
    public void Dispose() { _stop = true; }
}
