using System;
using System.IO;

namespace ScreenMirror;

/// <summary>Minimal thread-safe logger: console (when attached) plus a small log file in the data directory.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;
    private static bool _verbose;

    public static string? FilePath { get; private set; }

    public static void Init(string dataDir, bool verbose)
    {
        _verbose = verbose;
        try
        {
            Directory.CreateDirectory(dataDir);
            FilePath = Path.Combine(dataDir, "screenmirror.log");
            var fi = new FileInfo(FilePath);
            bool append = fi.Exists && fi.Length < 1_000_000;
            _file = new StreamWriter(new FileStream(FilePath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch
        {
            _file = null; // logging must never prevent startup
        }
    }

    public static void Debug(string msg) { if (_verbose) Write("DBG", msg); }
    public static void Info(string msg) => Write("INF", msg);
    public static void Warn(string msg) => Write("WRN", msg);
    public static void Error(string msg) => Write("ERR", msg);

    private static void Write(string level, string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {msg}";
        lock (Gate)
        {
            try { Console.WriteLine(line); } catch { }
            try { _file?.WriteLine(line); } catch { }
        }
    }
}
