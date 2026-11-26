using System.Diagnostics;
using AirSend.Core;
using NAudio.CoreAudioApi;

namespace AirSend.WinAudio;

/// <summary>
/// Mutes an app's audio on this PC (its Windows volume-mixer sessions) and restores it later.
/// Only sessions that AirSend muted itself are unmuted again. Remembers what it muted on disk,
/// so a crash doesn't leave the app muted forever.
/// </summary>
public sealed class LocalMute : IDisposable
{
    readonly string _exe;
    readonly HashSet<string> _muted = new();   // session instance ids we muted
    static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirSend", "muted-sessions.txt");

    public LocalMute(string exeName) { _exe = exeName; }

    /// <summary>IDs of output devices the app currently plays on.</summary>
    public List<string> DevicesInUse()
    {
        var ids = new List<string>();
        try
        {
            var pids = Process.GetProcessesByName(_exe).Select(p => { int id = p.Id; p.Dispose(); return id; }).ToHashSet();
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var mgr = dev.AudioSessionManager; mgr.RefreshSessions();
                    var ss = mgr.Sessions;
                    for (int i = 0; i < ss.Count; i++)
                        if (pids.Contains((int)ss[i].GetProcessID) && ss[i].State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) { ids.Add(dev.ID); break; }
                }
                catch { }
            }
        }
        catch { }
        return ids;
    }

    /// <summary>Mute every current session of the app. Call repeatedly; new sessions get muted too.</summary>
    public int Apply()
    {
        int n = 0;
        ForEachSession((s, id) =>
        {
            if (_muted.Contains(id)) { if (!s.SimpleAudioVolume.Mute) s.SimpleAudioVolume.Mute = true; n++; return; }
            if (s.SimpleAudioVolume.Mute) return; // the user had muted it already; leave it alone
            s.SimpleAudioVolume.Mute = true;
            _muted.Add(id); n++;
            Log.Info($"Muted '{_exe}' on this PC (session {Short(id)})");
        });
        Persist();
        return n;
    }

    public void Restore()
    {
        if (_muted.Count == 0) return;
        ForEachSession((s, id) =>
        {
            if (_muted.Contains(id) && s.SimpleAudioVolume.Mute) { s.SimpleAudioVolume.Mute = false; Log.Info($"Unmuted '{_exe}' on this PC"); }
        });
        _muted.Clear();
        Persist();
    }

    void ForEachSession(Action<AudioSessionControl, string> act)
    {
        try
        {
            var pids = Process.GetProcessesByName(_exe).Select(p => { int id = p.Id; p.Dispose(); return id; }).ToHashSet();
            if (pids.Count == 0) return;
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var mgr = dev.AudioSessionManager;
                    mgr.RefreshSessions();
                    var sessions = mgr.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        if (s.IsSystemSoundsSession || !pids.Contains((int)s.GetProcessID)) continue;
                        act(s, s.GetSessionInstanceIdentifier);
                    }
                }
                catch (Exception ex) { Log.Debug($"mute: {dev.FriendlyName}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Debug($"mute: {ex.Message}"); }
    }

    void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            if (_muted.Count == 0) { if (File.Exists(StateFile)) File.Delete(StateFile); return; }
            File.WriteAllLines(StateFile, new[] { _exe }.Concat(_muted));
        }
        catch { }
    }

    /// <summary>After a crash: unmute whatever a previous run left muted.</summary>
    public static void RecoverAfterCrash()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var lines = File.ReadAllLines(StateFile);
            if (lines.Length < 2) { File.Delete(StateFile); return; }
            var m = new LocalMute(lines[0]);
            foreach (var id in lines.Skip(1)) m._muted.Add(id);
            Log.Info($"Restoring sound of '{lines[0]}' that a previous run had muted");
            m.Restore();
        }
        catch (Exception ex) { Log.Debug($"mute recovery: {ex.Message}"); }
    }

    static string Short(string id) => id.Length > 40 ? "…" + id[^40..] : id;

    public void Dispose() => Restore();
}

/// <summary>Mutes / restores an output device (e.g. headphones).</summary>
public sealed class DeviceMute : IDisposable
{
    string? _id;
    bool _didMute;
    static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirSend", "muted-devices.txt");
    static readonly object FileLock = new();

    public DeviceMute(string? deviceId) { _id = deviceId; }
    public void Apply()
    {
        using var en = new MMDeviceEnumerator();
        using var d = _id == null ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : en.GetDevice(_id);
        _id = d.ID;
        if (!d.AudioEndpointVolume.Mute)
        {
            Remember(_id, true);
            d.AudioEndpointVolume.Mute = true; _didMute = true;
            Log.Info($"Muted device '{d.FriendlyName}'");
        }
    }
    public void Dispose()
    {
        if (!_didMute || _id == null) return;
        using var en = new MMDeviceEnumerator();
        using var d = en.GetDevice(_id);
        d.AudioEndpointVolume.Mute = false; _didMute = false;
        Remember(_id, false);
        Log.Info($"Unmuted device '{d.FriendlyName}'");
    }

    static void Remember(string id, bool add)
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
                var set = File.Exists(StateFile) ? File.ReadAllLines(StateFile).Where(l => l.Length > 0).ToHashSet() : new HashSet<string>();
                if (add) set.Add(id); else set.Remove(id);
                if (set.Count == 0) { if (File.Exists(StateFile)) File.Delete(StateFile); }
                else File.WriteAllLines(StateFile, set);
            }
            catch { }
        }
    }

    /// <summary>After a crash: unmute devices a previous run left muted.</summary>
    public static void RecoverAfterCrash()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            using var en = new MMDeviceEnumerator();
            foreach (var id in File.ReadAllLines(StateFile).Where(l => l.Length > 0))
            {
                try { using var d = en.GetDevice(id); d.AudioEndpointVolume.Mute = false; Log.Info($"Unmuted device '{d.FriendlyName}' left muted by a previous run"); } catch { }
            }
            File.Delete(StateFile);
        }
        catch (Exception ex) { Log.Debug($"device mute recovery: {ex.Message}"); }
    }
}
