using System.Text.Json;
using AirSend.Core;

namespace AirSend;

public sealed class AppSettings
{
    public SpeakerInfo? Speaker { get; set; }
    public List<SpeakerInfo> ManualSpeakers { get; set; } = new();
    public string SourceMode { get; set; } = "device"; // "device" | "app"
    public string? DeviceId { get; set; }               // null = default output device
    public string? AppExe { get; set; }
    public int Volume { get; set; } = 40;
    public int LatencyMs { get; set; } = 2000;
    public bool AutoStartStreaming { get; set; }
    public bool QuietAppLocally { get; set; } = true;
    public string? QuietDeviceId { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool StartMinimized { get; set; }

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirSend");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opts) ?? new();
        }
        catch (Exception ex) { Log.Warn($"Could not read settings: {ex.Message}"); }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Log.Warn($"Could not save settings: {ex.Message}"); }
    }
}

static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "AirSend";
    public static bool IsEnabled
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) is string;
        }
    }
    public static void Set(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --minimized");
        else k.DeleteValue(Name, false);
    }
}
