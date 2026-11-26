using System.Runtime.InteropServices;
using AirSend.Core;
using AirSend.WinAudio;

namespace AirSend;

static class Program
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static int Main(string[] args)
    {
        Log.Init(Path.Combine(AppSettings.Dir, "airsend.log"));
        Log.Info($"AirSend {Application.ProductVersion} starting on Windows {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
        timeBeginPeriod(1);

        if (args.Length > 0 && args[0].StartsWith("--") && args[0] != "--minimized")
        {
            AttachConsole(-1);
            Log.Console = true;
            try { return RunCli(args).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Error("Command failed", ex); return 1; }
        }

        using var mutex = new Mutex(true, "AirSend-single-instance-9c1f", out bool first);
        if (!first)
        {
            MessageBox.Show("AirSend is already running. Look for its icon in the taskbar notification area (bottom-right, maybe under the ^ arrow).", "AirSend", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        LocalMute.RecoverAfterCrash();
        DeviceMute.RecoverAfterCrash();
        AppRouteSession.RecoverPending();
        Application.ThreadException += (_, e) => Log.Error("UI error", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal error", e.ExceptionObject as Exception);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.Contains("--minimized")));
        return 0;
    }

    /// <summary>Command-line helpers used for testing/diagnostics.</summary>
    static async Task<int> RunCli(string[] args)
    {
        string Arg(string name, string def = "") { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
        switch (args[0])
        {
            case "--list":
            {
                var found = await MdnsBrowser.BrowseAsync(timeoutMs: 4000);
                foreach (var s in found) Log.Info($"SPEAKER {s.Name} | {s.Host}:{s.Port} | {string.Join(" ", s.Txt.Select(kv => kv.Key + "=" + kv.Value))}");
                foreach (var d in DeviceLoopbackCapture.ListDevices()) Log.Info($"DEVICE {d.name} | {d.id}");
                foreach (var a in AudioApps.List()) Log.Info($"APP {a.ExeName} | {a.Title} | audio={a.HasAudioSession}");
                Log.Info($"Per-app capture supported: {ProcessLoopbackCapture.IsSupported}");
                return 0;
            }
            case "--capture-test":
            {
                // --capture-test [--app Spotify] [--seconds 5]
                var app = Arg("--app");
                int secs = int.Parse(Arg("--seconds", "5"));
                var fifo = new AudioFifo();
                using IAudioCapture cap = string.IsNullOrEmpty(app) ? new DeviceLoopbackCapture(null) : new ProcessLoopbackCapture(app);
                cap.Start(fifo);
                for (int i = 0; i < secs * 2; i++)
                {
                    await Task.Delay(500);
                    Log.Info($"captured {fifo.TotalWritten} frames @ {fifo.SampleRate} Hz, peak {fifo.TakePeak():0.000}");
                    fifo.Skip(fifo.FramesAvailable);
                }
                return 0;
            }
            case "--mute-test":
            {
                // --mute-test --app Spotify : does app capture still get sound while the app / the device is muted?
                var app = Arg("--app");
                if (string.IsNullOrEmpty(app)) { Log.Error("--app is required"); return 2; }
                var fifo = new AudioFifo();
                using var cap = new ProcessLoopbackCapture(app);
                cap.Start(fifo);
                async Task<float> Phase(string name)
                {
                    fifo.TakePeak(); float peak = 0;
                    for (int i = 0; i < 8; i++) { await Task.Delay(400); peak = Math.Max(peak, fifo.TakePeak()); fifo.Skip(fifo.FramesAvailable); }
                    Log.Info($"MUTETEST {name}: peak {peak:0.000}");
                    return peak;
                }
                await Task.Delay(1500);
                await Phase("baseline");
                var lm = new LocalMute(app);
                try { Log.Info($"muted {lm.Apply()} session(s)"); await Phase("app-muted-in-mixer"); }
                finally { lm.Restore(); }
                await Phase("after-app-unmute");
                var dm = new DeviceMute(null);
                try { dm.Apply(); await Phase("default-device-muted"); }
                finally { dm.Dispose(); }
                await Phase("after-device-unmute");
                return 0;
            }
            case "--stream":
            {
                // --stream <speaker name or ip> [--tone] [--app Spotify] [--seconds 20] [--volume 30]
                string target = args.Length > 1 ? args[1] : "";
                var found = await MdnsBrowser.BrowseAsync(timeoutMs: 4000);
                var spk = found.FirstOrDefault(s => s.Name.Equals(target, StringComparison.OrdinalIgnoreCase) || s.Host == target)
                          ?? (System.Net.IPAddress.TryParse(target, out _) ? new SpeakerInfo { Name = target, Host = target, Port = int.Parse(Arg("--port", "7000")), Manual = true } : null);
                if (spk == null) { Log.Error($"Speaker '{target}' not found"); return 2; }
                var app = Arg("--app");
                Func<IAudioCapture> factory = args.Contains("--tone") ? () => new ToneCapture(440, 48000)
                    : string.IsNullOrEmpty(app) ? () => new DeviceLoopbackCapture(null) : () => new ProcessLoopbackCapture(app, Arg("--quiet-device") is { Length: > 0 } q ? q : null);
                using var eng = new StreamEngine(factory, spk, new EngineOptions { VolumePercent = int.Parse(Arg("--volume", "25")) });
                eng.Start();
                int secs = int.Parse(Arg("--seconds", "20"));
                for (int i = 0; i < secs; i++) { await Task.Delay(1000); if (eng.State is EngineState.Stopped or EngineState.Error) break; }
                eng.Stop();
                return eng.State == EngineState.Error ? 3 : 0;
            }
            default:
                Log.Info("Usage: AirSend.exe [--minimized] | --list | --capture-test [--app Name] | --stream <speaker> [--tone] [--app Name] [--seconds N] [--volume P]");
                return 0;
        }
    }
}
