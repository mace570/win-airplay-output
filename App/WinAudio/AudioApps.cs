using System.Diagnostics;
using System.Runtime.InteropServices;
using AirSend.Core;
using NAudio.CoreAudioApi;

namespace AirSend.WinAudio;

public sealed record AudioAppInfo(string ExeName, string Title, bool HasAudioSession)
{
    public override string ToString() => HasAudioSession ? $"{Title}  ({ExeName}.exe) 🔊" : $"{Title}  ({ExeName}.exe)";
}

public static class AudioApps
{
    static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    { "AirSend", "explorer", "TextInputHost", "ApplicationFrameHost", "SystemSettings", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "audiodg", "Idle", "System" };

    /// <summary>Apps that currently have an audio session, plus apps with a visible window.</summary>
    public static List<AudioAppInfo> List()
    {
        var withAudio = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in SessionPids())
        {
            try { using var p = Process.GetProcessById(pid); withAudio.Add(p.ProcessName); } catch { }
        }
        var result = new Dictionary<string, AudioAppInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string name = p.ProcessName;
                if (Ignore.Contains(name)) continue;
                bool audio = withAudio.Contains(name);
                string title = p.MainWindowHandle != IntPtr.Zero ? p.MainWindowTitle : "";
                if (!audio && string.IsNullOrWhiteSpace(title)) continue;
                string friendly = name;
                try { friendly = p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? d : name; } catch { }
                if (!result.ContainsKey(name) || audio)
                    result[name] = new AudioAppInfo(name, friendly, audio);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return result.Values.OrderByDescending(a => a.HasAudioSession).ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static List<int> SessionPids()
    {
        var pids = new List<int>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var sessions = dev.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        if (s.IsSystemSoundsSession) continue;
                        if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                        int pid = (int)s.GetProcessID;
                        if (pid > 0) pids.Add(pid);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Debug($"session enumeration failed: {ex.Message}"); }
        return pids;
    }

    /// <summary>Find the top-most process of an app (so the whole process tree is captured).</summary>
    public static int ResolveRootPid(string exeName)
    {
        var parents = ParentMap();
        var procs = Process.GetProcessesByName(exeName);
        try
        {
            if (procs.Length == 0) return 0;
            var ids = procs.Select(p => p.Id).ToHashSet();
            int Root(int pid)
            {
                int guard = 0;
                while (guard++ < 64 && parents.TryGetValue(pid, out var pp) && ids.Contains(pp) && pp != pid) pid = pp;
                return pid;
            }
            // Prefer the tree that is currently playing audio.
            foreach (var spid in SessionPids())
                if (ids.Contains(spid)) return Root(spid);
            return procs.Select(p => Root(p.Id)).GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref e))
            {
                do { map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; } while (Process32NextW(snap, ref e));
            }
        }
        finally { CloseHandle(snap); }
        return map;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
