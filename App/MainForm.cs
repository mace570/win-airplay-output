using AirSend.Core;
using AirSend.WinAudio;

namespace AirSend;

sealed class MainForm : Form
{
    readonly AppSettings _s = AppSettings.Load();
    readonly ComboBox _speakers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Button _refreshSpeakers = new() { Text = "Search again", AutoSize = true };
    readonly Button _addIp = new() { Text = "Add by IP…", AutoSize = true };
    readonly RadioButton _modeDevice = new() { Text = "All PC sound from:", AutoSize = true };
    readonly RadioButton _modeApp = new() { Text = "Only this app:", AutoSize = true };
    readonly ComboBox _devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ComboBox _apps = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly Button _refreshApps = new() { Text = "Refresh", AutoSize = true };
    readonly CheckBox _muteLocal = new() { Text = "Keep it off my PC, play it here:", AutoSize = true };
    readonly ComboBox _quietDevices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    readonly ToolTip _tip = new();
    string _lastStatus = "Ready.";
    readonly TrackBar _volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Dock = DockStyle.Fill, AutoSize = false, Height = 32 };
    readonly Label _volLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    readonly Button _startStop = new() { Text = "Start streaming", Height = 40, Dock = DockStyle.Fill, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold) };
    readonly ProgressBar _meter = new() { Minimum = 0, Maximum = 100, Dock = DockStyle.Fill, Height = 10, Style = ProgressBarStyle.Continuous };
    readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, Height = 40, Text = "Ready." };
    readonly CheckBox _autoStream = new() { Text = "Start streaming automatically when AirSend opens", AutoSize = true };
    readonly CheckBox _autoWin = new() { Text = "Open AirSend when Windows starts", AutoSize = true };
    readonly CheckBox _closeToTray = new() { Text = "Keep running in the tray when the window is closed", AutoSize = true };
    readonly LinkLabel _openLog = new() { Text = "Open log folder", AutoSize = true };
    readonly NotifyIcon _tray = new();
    readonly ToolStripMenuItem _trayStartStop = new("Start streaming");
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 200 };
    readonly bool _startMinimized;
    StreamEngine? _engine;
    bool _exiting, _loading;

    public MainForm(bool startMinimized)
    {
        _startMinimized = startMinimized;
        if (startMinimized) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }
        Text = "AirSend – PC audio to AirPlay";
        using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("AirSend.airsend.ico")) if (s != null) Icon = new Icon(s);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont!;
        ClientSize = new Size(580, 460);
        Padding = new Padding(12);
        BuildLayout();
        BuildTray();
        WireEvents();
        LoadSettingsIntoUi();
        int ticks = 0;
        _uiTimer.Tick += (_, _) =>
        {
            if (++ticks % 50 == 0 && _engine == null && AppRouteSession.HasPending) AppRouteSession.RecoverPending();
            _meter.Value = _engine == null ? 0 : (int)Math.Clamp(Math.Sqrt(_engine.Level) * 100, 0, 100);
            var note = _engine?.State == EngineState.Streaming ? _engine.CaptureNote : null;
            var shown = note == null ? _lastStatus : $"{_lastStatus} ({note})";
            if (_status.Text != shown) _status.Text = shown;
        };
        _uiTimer.Start();
    }

    void BuildLayout()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = false };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label L(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 3) };

        t.Controls.Add(L("Speaker:"), 0, 0); t.Controls.Add(_speakers, 1, 0);
        var sp = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        sp.Controls.Add(_refreshSpeakers); sp.Controls.Add(_addIp);
        t.Controls.Add(sp, 2, 0);

        t.Controls.Add(new Label { Height = 6 }, 0, 1);
        t.Controls.Add(_modeDevice, 0, 2); t.Controls.Add(_devices, 1, 2);
        t.SetColumnSpan(_devices, 2);
        t.Controls.Add(_modeApp, 0, 3); t.Controls.Add(_apps, 1, 3); t.Controls.Add(_refreshApps, 2, 3);
        if (!ProcessLoopbackCapture.IsSupported || !AppAudioRouting.IsSupported) { _muteLocal.Enabled = false; _quietDevices.Enabled = false; }
        if (!ProcessLoopbackCapture.IsSupported) { _modeApp.Enabled = false; _apps.Enabled = false; _refreshApps.Enabled = false; _modeApp.Text = "Only this app (needs newer Windows):"; }

        _muteLocal.Margin = new Padding(20, 4, 3, 6);
        t.Controls.Add(_muteLocal, 0, 4); t.Controls.Add(_quietDevices, 1, 4); t.SetColumnSpan(_quietDevices, 2);
        _tip.SetToolTip(_muteLocal, "While streaming, Windows sends this app's sound to the output picked here instead of your headphones.\nPick an output you don't listen to (e.g. unused speakers or a monitor without speakers).\nThe Sonos still gets the sound. It is put back when you stop streaming.");
        _tip.SetToolTip(_quietDevices, _tip.GetToolTip(_muteLocal));
        t.Controls.Add(L("Speaker volume:"), 0, 5); t.Controls.Add(_volume, 1, 5); t.Controls.Add(_volLabel, 2, 5);
        t.Controls.Add(_startStop, 0, 6); t.SetColumnSpan(_startStop, 3);
        t.Controls.Add(_meter, 0, 7); t.SetColumnSpan(_meter, 3);
        t.Controls.Add(_status, 0, 8); t.SetColumnSpan(_status, 3);
        t.Controls.Add(_autoStream, 0, 9); t.SetColumnSpan(_autoStream, 3);
        t.Controls.Add(_autoWin, 0, 10); t.SetColumnSpan(_autoWin, 3);
        t.Controls.Add(_closeToTray, 0, 11); t.SetColumnSpan(_closeToTray, 3);
        t.Controls.Add(_openLog, 0, 12); t.SetColumnSpan(_openLog, 3);
        Controls.Add(t);
    }

    void BuildTray()
    {
        _tray.Icon = Icon;
        _tray.Text = "AirSend";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open AirSend", null, (_, _) => ShowWindow());
        menu.Items.Add(_trayStartStop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _trayStartStop.Click += (_, _) => ToggleStreaming();
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    void WireEvents()
    {
        _refreshSpeakers.Click += async (_, _) => await RefreshSpeakersAsync();
        _addIp.Click += async (_, _) => await AddByIpAsync();
        _refreshApps.Click += (_, _) => RefreshApps();
        _startStop.Click += (_, _) => ToggleStreaming();
        _volume.ValueChanged += (_, _) =>
        {
            _volLabel.Text = $"{_volume.Value}%";
            if (_loading) return;
            _s.Volume = _volume.Value; _s.Save();
            _engine?.SetVolume(_volume.Value);
        };
        _speakers.SelectedIndexChanged += (_, _) => { if (!_loading) { _s.Speaker = _speakers.SelectedItem as SpeakerInfo; _s.Save(); RestartIfRunning(); } };
        _modeDevice.CheckedChanged += (_, _) => SourceChanged();
        _modeApp.CheckedChanged += (_, _) => SourceChanged();
        _devices.SelectedIndexChanged += (_, _) => SourceChanged();
        _apps.SelectedIndexChanged += (_, _) => SourceChanged();
        _apps.DropDown += (_, _) => RefreshApps(keepOpen: true);
        _muteLocal.CheckedChanged += (_, _) => { UpdateEnabled(); if (!_loading) { _s.QuietAppLocally = _muteLocal.Checked; _s.Save(); RestartIfRunning(); } };
        _quietDevices.SelectedIndexChanged += (_, _) => { if (!_loading) { _s.QuietDeviceId = (_quietDevices.SelectedItem as DeviceItem)?.Id; _s.Save(); RestartIfRunning(); } };
        _autoStream.CheckedChanged += (_, _) => { if (!_loading) { _s.AutoStartStreaming = _autoStream.Checked; _s.Save(); } };
        _closeToTray.CheckedChanged += (_, _) => { if (!_loading) { _s.CloseToTray = _closeToTray.Checked; _s.Save(); } };
        _autoWin.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            try { Autostart.Set(_autoWin.Checked); } catch (Exception ex) { Log.Error("Autostart", ex); }
        };
        _openLog.LinkClicked += (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", AppSettings.Dir); } catch { } };
    }

    sealed record DeviceItem(string? Id, string Name) { public override string ToString() => Name; }

    void LoadSettingsIntoUi()
    {
        _loading = true;
        try
        {
            // speakers: remembered one first, so auto-start works before discovery finishes
            _speakers.Items.Clear();
            if (_s.Speaker != null) _speakers.Items.Add(_s.Speaker);
            foreach (var m in _s.ManualSpeakers) if (!_speakers.Items.Cast<SpeakerInfo>().Any(x => x.Key == m.Key)) _speakers.Items.Add(m);
            if (_speakers.Items.Count > 0) _speakers.SelectedIndex = 0;

            _devices.Items.Clear();
            _devices.Items.Add(new DeviceItem(null, "Default output device (follows Windows setting)"));
            try { foreach (var d in DeviceLoopbackCapture.ListDevices()) _devices.Items.Add(new DeviceItem(d.id, d.name)); }
            catch (Exception ex) { Log.Error("Listing devices", ex); }
            _devices.SelectedItem = _devices.Items.Cast<DeviceItem>().FirstOrDefault(d => d.Id == _s.DeviceId) ?? _devices.Items[0];

            _quietDevices.Items.Clear();
            string? defaultId = null;
            try { using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator(); using var def = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia); defaultId = def.ID; } catch { }
            foreach (var d in _devices.Items.Cast<DeviceItem>().Where(d => d.Id != null)) _quietDevices.Items.Add(d);
            _quietDevices.SelectedItem = _quietDevices.Items.Cast<DeviceItem>().FirstOrDefault(d => d.Id == _s.QuietDeviceId)
                                         ?? _quietDevices.Items.Cast<DeviceItem>().FirstOrDefault(d => d.Id != defaultId);
            if (_quietDevices.Items.Count < 2) { _muteLocal.Text = "Keep it off my PC (needs a 2nd output, see README):"; }

            RefreshApps();
            _modeApp.Checked = _s.SourceMode == "app" && ProcessLoopbackCapture.IsSupported;
            _modeDevice.Checked = !_modeApp.Checked;
            _volume.Value = Math.Clamp(_s.Volume, 0, 100);
            _volLabel.Text = $"{_volume.Value}%";
            _autoStream.Checked = _s.AutoStartStreaming;
            _muteLocal.Checked = _s.QuietAppLocally;
            _closeToTray.Checked = _s.CloseToTray;
            try { _autoWin.Checked = Autostart.IsEnabled; } catch { }
        }
        finally { _loading = false; }
        UpdateEnabled();
    }

    void RefreshApps(bool keepOpen = false)
    {
        bool was = _loading; _loading = true;
        try
        {
            var current = (_apps.SelectedItem as AudioAppInfo)?.ExeName ?? _s.AppExe;
            List<AudioAppInfo> apps;
            try { apps = AudioApps.List(); } catch (Exception ex) { Log.Error("Listing apps", ex); apps = new(); }
            if (!string.IsNullOrEmpty(current) && !apps.Any(a => a.ExeName.Equals(current, StringComparison.OrdinalIgnoreCase)))
                apps.Insert(0, new AudioAppInfo(current!, current! + " (not running)", false));
            _apps.BeginUpdate();
            _apps.Items.Clear();
            foreach (var a in apps) _apps.Items.Add(a);
            _apps.SelectedItem = apps.FirstOrDefault(a => a.ExeName.Equals(current ?? "", StringComparison.OrdinalIgnoreCase));
            _apps.EndUpdate();
        }
        finally { _loading = was; }
    }

    void SourceChanged()
    {
        UpdateEnabled();
        if (_loading) return;
        _s.SourceMode = _modeApp.Checked ? "app" : "device";
        _s.DeviceId = (_devices.SelectedItem as DeviceItem)?.Id;
        if (_apps.SelectedItem is AudioAppInfo a) _s.AppExe = a.ExeName;
        _s.Save();
        RestartIfRunning();
    }

    void UpdateEnabled()
    {
        _devices.Enabled = _modeDevice.Checked;
        _apps.Enabled = _modeApp.Checked;
        _refreshApps.Enabled = _modeApp.Checked;
        bool routeOk = ProcessLoopbackCapture.IsSupported && AppAudioRouting.IsSupported;
        _muteLocal.Enabled = _modeApp.Checked && routeOk;
        _quietDevices.Enabled = _modeApp.Checked && routeOk && _muteLocal.Checked;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_startMinimized) Hide();
        if (_s.AutoStartStreaming && _s.Speaker != null) StartStreaming();
        await RefreshSpeakersAsync();
    }

    async Task RefreshSpeakersAsync()
    {
        _refreshSpeakers.Enabled = false;
        if (_engine == null) SetStatus("Searching for AirPlay speakers…");
        try
        {
            var found = await Task.Run(() => MdnsBrowser.BrowseAsync(timeoutMs: 3500));
            _loading = true;
            var selectedKey = (_speakers.SelectedItem as SpeakerInfo)?.Name;
            var list = new List<SpeakerInfo>(found);
            foreach (var m in _s.ManualSpeakers) if (!list.Any(x => x.Host == m.Host)) list.Add(m);
            // keep the remembered speaker (even if not found right now), but update its address if it moved
            if (_s.Speaker != null)
            {
                var same = list.FirstOrDefault(x => x.Name.Equals(_s.Speaker.Name, StringComparison.OrdinalIgnoreCase));
                if (same != null) { _s.Speaker = same; _s.Save(); }
                else list.Insert(0, _s.Speaker);
            }
            _speakers.BeginUpdate();
            _speakers.Items.Clear();
            foreach (var s in list) _speakers.Items.Add(s);
            _speakers.SelectedItem = list.FirstOrDefault(x => x.Name == selectedKey) ?? list.FirstOrDefault();
            _speakers.EndUpdate();
            if (_s.Speaker == null && _speakers.SelectedItem is SpeakerInfo first) { _s.Speaker = first; _s.Save(); }
            _loading = false;
            if (_engine == null)
                SetStatus(found.Count == 0
                    ? "No AirPlay speakers found. Check that the PC is on the same Wi-Fi/network as the speakers, and that Windows Firewall allowed AirSend. You can also use \"Add by IP…\"."
                    : $"Found {found.Count} speaker(s). Pick one and press Start.");
        }
        catch (Exception ex) { Log.Error("Discovery", ex); _loading = false; }
        finally { _refreshSpeakers.Enabled = true; }
    }

    async Task AddByIpAsync()
    {
        var ip = Prompt("Enter the speaker's IP address (find it in the Sonos app: Settings → System → About My System):", "Add speaker by IP");
        if (string.IsNullOrWhiteSpace(ip)) return;
        ip = ip.Trim();
        SetStatus($"Checking {ip}…");
        var info = await MdnsBrowser.ProbeAsync(ip, 7000);
        if (info == null) { SetStatus($"Could not reach an AirPlay speaker at {ip} (port 7000)."); return; }
        if (info.Manual) _s.ManualSpeakers.Add(info);
        _speakers.Items.Add(info);
        _speakers.SelectedItem = info;
        _s.Speaker = info; _s.Save();
        SetStatus($"Added {info.Name}.");
    }

    static string? Prompt(string text, string caption)
    {
        using var f = new Form { Text = caption, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(420, 130), Font = SystemFonts.MessageBoxFont! };
        var l = new Label { Text = text, Left = 10, Top = 10, Width = 400, Height = 40 };
        var tb = new TextBox { Left = 10, Top = 55, Width = 400 };
        var ok = new Button { Text = "OK", Left = 250, Top = 90, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 335, Top = 90, Width = 75, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { l, tb, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
    }

    void ToggleStreaming()
    {
        if (_engine != null) StopStreaming(); else StartStreaming();
    }

    void StartStreaming()
    {
        if (_engine != null) return;
        var spk = _speakers.SelectedItem as SpeakerInfo ?? _s.Speaker;
        if (spk == null) { SetStatus("Pick a speaker first."); return; }
        Func<IAudioCapture> factory;
        if (_modeApp.Checked)
        {
            var exe = (_apps.SelectedItem as AudioAppInfo)?.ExeName ?? _s.AppExe;
            if (string.IsNullOrEmpty(exe)) { SetStatus("Pick an app first (start playing something in it, then press Refresh)."); return; }
            string? quiet = _muteLocal.Checked && _muteLocal.Enabled ? (_quietDevices.SelectedItem as DeviceItem)?.Id : null;
            factory = () => new ProcessLoopbackCapture(exe, quiet);
        }
        else
        {
            var id = (_devices.SelectedItem as DeviceItem)?.Id;
            factory = () => new DeviceLoopbackCapture(id);
        }
        var eng = new StreamEngine(factory, spk, new EngineOptions { VolumePercent = _volume.Value, LatencyMs = _s.LatencyMs });
        eng.StatusChanged += (state, text) => BeginInvokeSafe(() => OnEngineStatus(eng, state, text));
        eng.SpeakerMoved += s => BeginInvokeSafe(() => { _s.Speaker = s; _s.Save(); });
        _engine = eng;
        eng.Start();
        _startStop.Text = "Stop streaming";
        _trayStartStop.Text = "Stop streaming";
    }

    void StopStreaming()
    {
        var eng = _engine;
        _engine = null;
        _startStop.Text = "Start streaming";
        _trayStartStop.Text = "Start streaming";
        if (eng == null) return;
        _startStop.Enabled = false;
        Task.Run(() => { eng.Stop(); }).ContinueWith(_ => BeginInvokeSafe(() => { _startStop.Enabled = true; SetStatus("Stopped."); }));
    }

    void RestartIfRunning()
    {
        if (_engine == null) return;
        var eng = _engine;
        _engine = null;
        Task.Run(() => eng.Stop()).ContinueWith(_ => BeginInvokeSafe(StartStreaming));
    }

    void OnEngineStatus(StreamEngine eng, EngineState state, string text)
    {
        if (_engine != eng) return;
        SetStatus(text);
        if (state is EngineState.Stopped or EngineState.Error)
        {
            _engine = null;
            _startStop.Text = "Start streaming";
            _trayStartStop.Text = "Start streaming";
            if (state == EngineState.Error || text.Contains("ended"))
                _tray.ShowBalloonTip(4000, "AirSend", text, ToolTipIcon.Info);
        }
    }

    void SetStatus(string text)
    {
        _lastStatus = text;
        _status.Text = text;
        var tip = "AirSend – " + text;
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    void ShowWindow()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && _s.CloseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(2500, "AirSend", "Still running here. Right-click the icon to exit.", ToolTipIcon.Info);
            return;
        }
        _uiTimer.Stop();
        var eng = _engine; _engine = null;
        eng?.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }
}
