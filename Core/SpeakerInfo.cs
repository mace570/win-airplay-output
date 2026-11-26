using System.Net;

namespace AirSend.Core;

public sealed class SpeakerInfo
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";      // IPv4 address
    public int Port { get; set; } = 7000;
    public Dictionary<string, string> Txt { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Manual { get; set; }

    public string Model => Txt.TryGetValue("am", out var am) ? am : "";

    public IEnumerable<string> EncryptionTypes => Txt.TryGetValue("et", out var et) ? et.Split(',', StringSplitOptions.TrimEntries) : Array.Empty<string>();

    /// <summary>Many AirPlay-2 devices (e.g. newer Sonos) want POST /auth-setup before ANNOUNCE.</summary>
    public bool WantsAuthSetup => EncryptionTypes.Contains("4");

    public override string ToString() => string.IsNullOrEmpty(Model) ? $"{Name} ({Host})" : $"{Name} – {Model} ({Host})";

    public string Key => $"{Host}:{Port}";
}
