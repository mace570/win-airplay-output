using System.Text;

namespace AirSend.Core;

/// <summary>Very small thread-safe file + console logger.</summary>
public static class Log
{
    static readonly object Gate = new();
    static string? _path;
    public static bool Console { get; set; }
    public static event Action<string>? Line;

    public static void Init(string path)
    {
        lock (Gate)
        {
            _path = path;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 2_000_000)
                {
                    var old = path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
            }
            catch { }
        }
    }

    public static string? FilePath => _path;

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex.GetType().Name}: {ex.Message}");
    public static void Debug(string msg) => Write("DEBUG", msg);

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}";
        lock (Gate)
        {
            if (Console) System.Console.WriteLine(line);
            if (_path != null)
            {
                try { File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); } catch { }
            }
        }
        try { Line?.Invoke(line); } catch { }
    }
}
