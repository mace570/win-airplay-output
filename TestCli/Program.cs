using AirSend.Core;

Log.Console = true;
if (args.Length > 0 && args[0] == "list")
{
    foreach (var s in await MdnsBrowser.BrowseAsync()) Console.WriteLine($"{s} port {s.Port} txt: {string.Join(" ", s.Txt.Select(kv => kv.Key + "=" + kv.Value))}");
    return;
}
var host = args.Length > 0 ? args[0] : "127.0.0.1";
int port = args.Length > 1 ? int.Parse(args[1]) : 5000;
int seconds = args.Length > 2 ? int.Parse(args[2]) : 8;
var spk = new SpeakerInfo { Name = "test", Host = host, Port = port };
if (args.Length > 3) spk.Txt["et"] = args[3];
using var eng = new StreamEngine(() => new ToneCapture(440, 48000), spk, new EngineOptions { LatencyMs = 2000, VolumePercent = 80 });
eng.Start();
await Task.Delay(seconds * 1000);
eng.Stop();
