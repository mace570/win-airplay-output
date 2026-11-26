using System.Diagnostics;
using System.Runtime.InteropServices;
using AirSend.Core;

namespace AirSend.WinAudio;

/// <summary>
/// Captures only the audio of one app (and its child processes) using WASAPI process loopback
/// (Windows 10 build 20348+ / Windows 11). Finds the app by its .exe name and re-attaches if it restarts.
/// </summary>
public sealed class ProcessLoopbackCapture : IAudioCapture
{
    const int Rate = 44100;
    readonly string _exeName; // e.g. "Spotify" (no .exe)
    Thread? _thread;
    volatile bool _stop;

    readonly bool _muteLocally;
    public ProcessLoopbackCapture(string exeName, bool muteLocally = false) { _exeName = exeName; _muteLocally = muteLocally; }
    public string Description => $"Only the app '{_exeName}'";
    public string? Note { get; private set; }

    // ---- "mute on this PC" with self-check ----
    enum MuteState { WaitForSound, CheckingApp, MutedApp, WaitForSoundDevice, CheckingDevice, MutedDevice, Failed }
    MuteState _ms = MuteState.WaitForSound;
    LocalMute? _appMute;
    readonly List<DeviceMute> _devMutes = new();
    double _soundSince = -1, _checkStart, _lastSoundAt = -1, _nextReapply;
    const float SoundThreshold = 0.003f;

    void MuteTick(float windowPeak)
    {
        if (!_muteLocally || _ms == MuteState.Failed) return;
        double now = NtpClock.Now;
        bool sound = windowPeak > SoundThreshold;
        if (sound) { _lastSoundAt = now; if (_soundSince < 0) _soundSince = now; } else _soundSince = -1;
        switch (_ms)
        {
            case MuteState.WaitForSound:
                if (_soundSince >= 0 && now - _soundSince >= 1.0)
                {
                    _appMute ??= new LocalMute(_exeName);
                    int n = _appMute.Apply();
                    Log.Info($"Local mute: muted {n} session(s) of '{_exeName}', checking the stream still gets sound…");
                    _ms = MuteState.CheckingApp; _checkStart = now; _lastSoundAt = -1;
                }
                break;
            case MuteState.CheckingApp:
                if (_lastSoundAt > _checkStart + 0.7) { _ms = MuteState.MutedApp; Note = "muted on this PC"; Log.Info("Local mute: app muted on this PC and the stream still has sound"); }
                else if (now - _checkStart > 3.0)
                {
                    Log.Info("Local mute: muting the app also silenced the stream; unmuting it and trying the output device instead");
                    _appMute!.Restore();
                    _ms = MuteState.WaitForSoundDevice; _soundSince = -1;
                }
                break;
            case MuteState.MutedApp:
                if (now >= _nextReapply) { _nextReapply = now + 2; _appMute!.Apply(); } // catch new sessions (e.g. new tab/track)
                break;
            case MuteState.WaitForSoundDevice:
                if (_soundSince >= 0 && now - _soundSince >= 1.0)
                {
                    var ids = _appMute!.DevicesInUse();
                    if (ids.Count == 0) ids.Add(null!);
                    foreach (var id in ids) { var d = new DeviceMute(id); try { d.Apply(); _devMutes.Add(d); } catch (Exception ex) { Log.Debug($"device mute: {ex.Message}"); } }
                    _ms = MuteState.CheckingDevice; _checkStart = now; _lastSoundAt = -1;
                }
                break;
            case MuteState.CheckingDevice:
                if (_lastSoundAt > _checkStart + 0.7) { _ms = MuteState.MutedDevice; Note = "PC output muted"; Log.Info("Local mute: output device muted and the stream still has sound"); }
                else if (now - _checkStart > 3.0)
                {
                    Log.Warn("Local mute: muting the output device also silenced the stream; giving up on local mute");
                    RestoreMute();
                    _ms = MuteState.Failed; Note = "couldn't mute on this PC without silencing the stream";
                }
                break;
        }
    }

    void RestoreMute()
    {
        try { _appMute?.Restore(); } catch (Exception ex) { Log.Debug($"restore: {ex.Message}"); }
        foreach (var d in _devMutes) { try { d.Dispose(); } catch (Exception ex) { Log.Debug($"restore: {ex.Message}"); } }
        _devMutes.Clear();
    }
    public bool IsAlive => !_stop;

    public static bool IsSupported => Environment.OSVersion.Version.Build >= 20348;

    public void Start(AudioFifo fifo)
    {
        _thread = new Thread(() => Loop(fifo)) { IsBackground = true, Name = "App capture", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    void Loop(AudioFifo fifo)
    {
        bool waitingLogged = false;
        while (!_stop)
        {
            int pid = AudioApps.ResolveRootPid(_exeName);
            if (pid <= 0)
            {
                if (!waitingLogged) { Log.Info($"Waiting for '{_exeName}' to start…"); waitingLogged = true; }
                Sleep(2000);
                continue;
            }
            waitingLogged = false;
            try { CaptureProcess(pid, fifo); }
            catch (Exception ex) { Log.Error($"App capture of '{_exeName}' (pid {pid}) failed", ex); Sleep(3000); }
        }
    }

    void Sleep(int ms) { for (int i = 0; i < ms / 100 && !_stop; i++) Thread.Sleep(100); }

    void CaptureProcess(int pid, AudioFifo fifo)
    {
        Process? proc = null;
        try { proc = Process.GetProcessById(pid); } catch { }
        var client = Activate(pid);
        IntPtr fmtPtr = IntPtr.Zero, evt = IntPtr.Zero;
        object? ccObj = null;
        try
        {
            var wf = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = Rate, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = Rate * 4, cbSize = 0 };
            fmtPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
            Marshal.StructureToPtr(wf, fmtPtr, false);
            const uint LOOPBACK = 0x00020000, EVENTCALLBACK = 0x00040000, AUTOCONVERTPCM = 0x80000000, SRC_DEFAULT_QUALITY = 0x08000000;
            int hr = client.Initialize(0, LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY, 2_000_000, 0, fmtPtr, IntPtr.Zero);
            if (hr < 0)
            {
                Log.Debug($"Initialize with auto-convert failed (0x{hr:X8}); retrying plain");
                Marshal.ReleaseComObject(client);
                client = Activate(pid);
                hr = client.Initialize(0, LOOPBACK | EVENTCALLBACK, 2_000_000, 0, fmtPtr, IntPtr.Zero);
            }
            Check(hr, "IAudioClient.Initialize");
            evt = CreateEventW(IntPtr.Zero, false, false, null);
            Check(client.SetEventHandle(evt), "SetEventHandle");
            var iid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref iid, out ccObj), "GetService(IAudioCaptureClient)");
            var cc = (IAudioCaptureClient)ccObj;
            Check(client.Start(), "Start");
            Log.Info($"App capture started for '{_exeName}' (pid {pid})");

            var stereo = new float[Rate * 2];
            double nextCheck = NtpClock.Now + 2;
            double nextMuteTick = NtpClock.Now + 0.5;
            float windowPeak = 0;
            while (!_stop)
            {
                WaitForSingleObject(evt, 50);
                while (true)
                {
                    Check(cc.GetNextPacketSize(out uint packet), "GetNextPacketSize");
                    if (packet == 0) break;
                    Check(cc.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _), "GetBuffer");
                    int n = (int)frames;
                    if (n * 2 > stereo.Length) stereo = new float[n * 2];
                    if ((flags & 0x2) != 0) Array.Clear(stereo, 0, n * 2);
                    else PcmConvert.ToStereo(data, n, 2, SampleKind.Int16, stereo);
                    cc.ReleaseBuffer(frames);
                    fifo.Write(stereo.AsSpan(0, n * 2), Rate);
                    if (_muteLocally) for (int i = 0; i < n * 2; i++) { float a = Math.Abs(stereo[i]); if (a > windowPeak) windowPeak = a; }
                }
                if (NtpClock.Now >= nextMuteTick)
                {
                    nextMuteTick = NtpClock.Now + 0.5;
                    try { MuteTick(windowPeak); } catch (Exception ex) { Log.Debug($"mute tick: {ex.Message}"); }
                    windowPeak = 0;
                }
                if (NtpClock.Now >= nextCheck)
                {
                    nextCheck = NtpClock.Now + 2;
                    if (proc != null) { try { if (proc.HasExited) { Log.Info($"'{_exeName}' closed; waiting for it to start again"); break; } } catch { } }
                }
            }
            client.Stop();
        }
        finally
        {
            if (_muteLocally) { RestoreMute(); if (_ms != MuteState.Failed) { _ms = MuteState.WaitForSound; Note = null; } }
            if (ccObj != null) Marshal.ReleaseComObject(ccObj);
            Marshal.ReleaseComObject(client);
            if (fmtPtr != IntPtr.Zero) Marshal.FreeHGlobal(fmtPtr);
            if (evt != IntPtr.Zero) CloseHandle(evt);
            proc?.Dispose();
        }
    }

    static void Check(int hr, string what) { if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr); }

    static IAudioClient Activate(int pid)
    {
        var p = new AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = 1 /*PROCESS_LOOPBACK*/, TargetProcessId = (uint)pid, ProcessLoopbackMode = 0 /*include tree*/ };
        IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
        IntPtr pVar = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT_BLOB>());
        try
        {
            Marshal.StructureToPtr(p, pParams, false);
            var pv = new PROPVARIANT_BLOB { vt = 65 /*VT_BLOB*/, cbSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(), pBlobData = pParams };
            Marshal.StructureToPtr(pv, pVar, false);
            var handler = new CompletionHandler();
            var iid = typeof(IAudioClient).GUID;
            int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, pVar, handler, out var op);
            Check(hr, "ActivateAudioInterfaceAsync");
            if (!handler.Done.WaitOne(5000)) throw new TimeoutException("Audio activation timed out");
            Check(handler.Result, "Process loopback activation");
            GC.KeepAlive(op);
            return (IAudioClient)handler.Client!;
        }
        finally
        {
            Marshal.FreeHGlobal(pVar);
            Marshal.FreeHGlobal(pParams);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(3000);
        if (_muteLocally) RestoreMute(); // idempotent safety net
    }

    // ---------------- interop ----------------
    [StructLayout(LayoutKind.Sequential)]
    struct AUDIOCLIENT_ACTIVATION_PARAMS { public int ActivationType; public uint TargetProcessId; public int ProcessLoopbackMode; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT_BLOB { public ushort vt, r1, r2, r3; public uint cbSize; public IntPtr pBlobData; }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX { public ushort wFormatTag; public ushort nChannels; public uint nSamplesPerSec; public uint nAvgBytesPerSec; public ushort nBlockAlign; public ushort wBitsPerSample; public ushort cbSize; }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid,
        IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation activationOperation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initialState, string? name);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op); }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface); }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None), ComVisible(true)]
    sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new(false);
        public int Result;
        public object? Client;
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out Result, out var obj);
                Client = obj;
            }
            catch (Exception ex) { Result = ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005); }
            finally { Done.Set(); }
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
