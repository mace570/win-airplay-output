using System.Diagnostics;
using System.Runtime.InteropServices;
using AirSend.Core;

namespace AirSend.WinAudio;

/// <summary>
/// Per-app output device ("App volume and device preferences" in Windows Settings), via the
/// undocumented AudioPolicyConfig factory (same approach as EarTrumpet). Windows 10 21H2+ / 11.
/// </summary>
public static class AppAudioRouting
{
    const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    const string Token = @"\\?\SWD#MMDEVAPI#";

    public static bool IsSupported => Environment.OSVersion.Version.Build >= 21390;

    static IAudioPolicyConfigFactory Factory()
    {
        const string cls = "Windows.Media.Internal.AudioPolicyConfig";
        Check(WindowsCreateString(cls, (uint)cls.Length, out IntPtr hcls), "WindowsCreateString");
        try
        {
            var iid = typeof(IAudioPolicyConfigFactory).GUID;
            Check(RoGetActivationFactory(hcls, ref iid, out IntPtr pf), "RoGetActivationFactory");
            try { return (IAudioPolicyConfigFactory)Marshal.GetObjectForIUnknown(pf); }
            finally { Marshal.Release(pf); }
        }
        finally { WindowsDeleteString(hcls); }
    }

    /// <summary>Returns the app's chosen output device id, or null if it follows the Windows default.</summary>
    public static string? Get(int pid)
    {
        var f = Factory();
        int hr = f.GetPersistedDefaultAudioEndpoint((uint)pid, 0 /*eRender*/, 1 /*eMultimedia*/, out IntPtr h);
        if (hr < 0 || h == IntPtr.Zero) return null;
        try
        {
            var s = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(h, out _));
            if (string.IsNullOrEmpty(s)) return null;
            if (s.StartsWith(Token)) s = s[Token.Length..];
            if (s.EndsWith(RenderSuffix)) s = s[..^RenderSuffix.Length];
            return s;
        }
        finally { WindowsDeleteString(h); }
    }

    /// <summary>Sets the app's output device; null = back to the Windows default.</summary>
    public static void Set(int pid, string? deviceId)
    {
        var f = Factory();
        IntPtr h = IntPtr.Zero;
        if (!string.IsNullOrEmpty(deviceId))
        {
            var full = Token + deviceId + RenderSuffix;
            Check(WindowsCreateString(full, (uint)full.Length, out h), "WindowsCreateString");
        }
        try
        {
            Check(f.SetPersistedDefaultAudioEndpoint((uint)pid, 0, 1 /*eMultimedia*/, h), "SetPersistedDefaultAudioEndpoint");
            Check(f.SetPersistedDefaultAudioEndpoint((uint)pid, 0, 0 /*eConsole*/, h), "SetPersistedDefaultAudioEndpoint");
        }
        finally { if (h != IntPtr.Zero) WindowsDeleteString(h); }
    }

    static void Check(int hr, string what) { if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr); }

    [DllImport("combase.dll", PreserveSig = true)] static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string src, uint len, out IntPtr hstring);
    [DllImport("combase.dll", PreserveSig = true)] static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll", PreserveSig = true)] static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint len);

    // IInspectable-derived; declared as IUnknown with the 3 IInspectable slots first.
    [ComImport, Guid("ab3d4648-e242-459f-b02f-541c70306324"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioPolicyConfigFactory
    {
        void _GetIids(); void _GetRuntimeClassName(); void _GetTrustLevel();
        void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9(); void _10();
        void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17(); void _18(); void _19();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, IntPtr deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint processId, int flow, int role, out IntPtr deviceId);
        [PreserveSig] int ClearAllPersistedApplicationDefaultEndpoints();
    }
}

/// <summary>
/// While streaming, moves an app's PC sound to a "quiet" output device and puts it back afterwards.
/// Remembers what it changed on disk so it can undo it after a crash or once the app runs again.
/// </summary>
public sealed class AppRouteSession : IDisposable
{
    readonly string _exe, _targetId;
    bool _applied;
    string? _original;
    static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirSend", "routed-app.txt");
    static readonly object FileLock = new();

    public AppRouteSession(string exe, string targetDeviceId) { _exe = exe; _targetId = targetDeviceId; }

    static List<int> Pids(string exe) => Process.GetProcessesByName(exe).Select(p => { int id = p.Id; p.Dispose(); return id; }).ToList();

    /// <summary>Route all current processes of the app. Safe to call repeatedly.</summary>
    public bool Apply()
    {
        var pids = Pids(_exe);
        if (pids.Count == 0) return false;
        if (!_applied)
        {
            _original = AppAudioRouting.Get(pids[0]);
            if (_original == _targetId) _original = null;
            Write(_exe, _original);
        }
        foreach (var pid in pids)
        {
            try
            {
                if (_applied && AppAudioRouting.Get(pid) == _targetId) continue;
                AppAudioRouting.Set(pid, _targetId);
            }
            catch (Exception ex) { Log.Debug($"route pid {pid}: {ex.Message}"); }
        }
        if (!_applied) Log.Info($"Moved '{_exe}' PC sound to the quiet output while streaming");
        _applied = true;
        return true;
    }

    public void Restore()
    {
        if (!_applied) return;
        if (RestoreNow(_exe, _original)) { _applied = false; Clear(); }
    }

    static bool RestoreNow(string exe, string? original)
    {
        var pids = Pids(exe);
        if (pids.Count == 0) { Log.Debug($"'{exe}' isn't running; will put its sound back next time it runs"); return false; }
        foreach (var pid in pids) { try { AppAudioRouting.Set(pid, original); } catch (Exception ex) { Log.Debug($"unroute pid {pid}: {ex.Message}"); } }
        Log.Info($"Put '{exe}' PC sound back on {(original == null ? "the Windows default output" : "its previous output")}");
        return true;
    }

    static void Write(string exe, string? original)
    {
        lock (FileLock) { try { Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!); File.WriteAllLines(StateFile, new[] { exe, original ?? "" }); } catch { } }
    }
    static void Clear() { lock (FileLock) { try { if (File.Exists(StateFile)) File.Delete(StateFile); } catch { } } }

    /// <summary>Undo a routing left over from an earlier run (crash, or app was closed when streaming stopped).</summary>
    public static void RecoverPending()
    {
        try
        {
            string[] l;
            lock (FileLock) { if (!File.Exists(StateFile)) return; l = File.ReadAllLines(StateFile); }
            if (l.Length < 1 || l[0].Length == 0) { Clear(); return; }
            if (RestoreNow(l[0], l.Length > 1 && l[1].Length > 0 ? l[1] : null)) Clear();
        }
        catch (Exception ex) { Log.Debug($"route recovery: {ex.Message}"); }
    }

    public static bool HasPending { get { lock (FileLock) return File.Exists(StateFile); } }

    public void Dispose() => Restore();
}
