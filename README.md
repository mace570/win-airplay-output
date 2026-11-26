# AirSend – play your Windows PC's sound on an AirPlay speaker

AirSend is a small Windows app that sends your PC's audio to an AirPlay speaker such as a **Sonos One**.
It's made for music: the sound is sent lossless (CD quality, 44.1 kHz / 16-bit) and arrives about 2 seconds
after it plays on the PC, which is normal for AirPlay.

You can send either

* **all PC sound** from an output device (whatever you hear on the PC), or
* **only one app**, e.g. just Spotify or just your browser (Windows 11 or Windows 10 21H2+).

## Install

1. Download **AirSend.exe** from the [latest release](../../releases/latest) (one file, nothing else to install).
2. Put it somewhere permanent, e.g. `C:\Users\<you>\Apps\AirSend\AirSend.exe`.
3. Double-click it.
   * If Windows shows **"Windows protected your PC"**, click **More info → Run anyway** (the app isn't code-signed).
   * If Windows Firewall asks, tick **Private networks** and click **Allow access**. AirSend needs this to find and talk to the speakers.

## Use

1. **Speaker:** pick your speaker (AirSend searches the network when it opens; press *Search again* if it isn't listed yet).
2. **Source:** choose *All PC sound* (and the output device), or *Only this app* and pick the app.
   Tip: start playing something in the app first, then press *Refresh*. Apps that are playing sound have a 🔊.
3. Press **Start streaming**. The bar under the button shows the audio level being sent.
4. Use the **Speaker volume** slider to set the Sonos volume.

Closing the window keeps AirSend running in the notification area (bottom-right, maybe under the **^** arrow).
Right-click the icon to start/stop or exit. Tick *Open AirSend when Windows starts* and
*Start streaming automatically* if you want it to always be on.

### Good to know

* **You'll hear the music on the PC too**, 2 seconds earlier than on the Sonos. Turn the PC speakers/headphones down,
  or choose an output device you don't listen to (e.g. a monitor's HDMI output) as Windows' output for the music app.
* **One speaker at a time.** Starting AirSend takes over the speaker; if someone starts playing from the Sonos app,
  the Sonos stops AirSend's stream and AirSend shows a note instead of grabbing the speaker back.
* If the network drops or the speaker reboots, AirSend reconnects by itself (and finds the speaker again if its IP changed).
* If the app you picked is closed, AirSend waits and picks it up again when you reopen it.

## Troubleshooting

| Problem | Try this |
|---|---|
| No speakers found | PC and Sonos must be on the same home network (not a guest Wi-Fi). Allow AirSend in Windows Firewall (*Windows Security → Firewall → Allow an app*). Or use **Add by IP…** with the IP from the Sonos app (*Settings → System → About My System*). |
| "Speaker asks for an AirPlay password" | Turn off the AirPlay password for the speaker in the Sonos app. |
| Connected but silent | Check the level bar moves. If not, the chosen source isn't playing (pick the right output device or app). If the bar moves but the Sonos is silent, send the log (below). |
| Anything else | Click **Open log folder** in the app and share `airsend.log`. |

Settings and the log live in `%LOCALAPPDATA%\AirSend`.

## How it works (for the curious)

* Audio capture: WASAPI loopback (whole device) or WASAPI *process loopback* (one app and its child processes).
* Audio is resampled to 44.1 kHz with a high-quality sinc resampler, with tiny (≤0.2 %) rate corrections to stay in step with the network clock.
* Streaming: classic AirPlay (RAOP) — RTSP + RTP/UDP with uncompressed ALAC frames, NTP timing replies, packet retransmission,
  and `auth-setup` for newer Sonos firmware.
* Speaker discovery: built-in mDNS/Bonjour browser (no Bonjour install needed).

## Build from source

Needs the .NET 8 SDK.

```
dotnet publish App/AirSend.csproj -c Release -o publish
```

`TestCli` is a small console tool that streams a test tone to any AirPlay receiver (`dotnet run --project TestCli -- <ip> <port> <seconds>`).

Command-line diagnostics of the Windows app: `AirSend.exe --list`, `AirSend.exe --capture-test [--app Spotify]`,
`AirSend.exe --stream "<speaker name or IP>" [--tone] [--app Spotify] [--seconds 20] [--volume 25]`.
