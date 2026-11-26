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
    readonly string? _id;
    bool _didMute;
    public DeviceMute(string? deviceId) { _id = deviceId; }
    public void Apply()
    {
        using var en = new MMDeviceEnumerator();
        using var d = _id == null ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : en.GetDevice(_id);
        if (!d.AudioEndpointVolume.Mute) { d.AudioEndpointVolume.Mute = true; _didMute = true; Log.Info($"Muted device '{d.FriendlyName}'"); }
    }
    public void Dispose()
    {
        if (!_didMute) return;
        using var en = new MMDeviceEnumerator();
        using var d = _id == null ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : en.GetDevice(_id);
        d.AudioEndpointVolume.Mute = false; _didMute = false;
        Log.Info($"Unmuted device '{d.FriendlyName}'");
    }
}
