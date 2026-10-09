using System;
using System.IO;

namespace ScreenMirror;

/// <summary>Command line options.</summary>
public sealed class Options
{
    public const int DefaultPort = 8642;

    public int Port { get; private set; } = DefaultPort;
    public bool PortExplicit { get; private set; }
    public bool NoTray { get; private set; }
    public bool NoBrowser { get; private set; }
    public bool Verbose { get; private set; }
    public string? WebRoot { get; private set; }
    public string DataDir { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenMirror");

    public static string Usage =>
        "ScreenMirror [--port N] [--no-tray] [--no-browser] [--data-dir PATH] [--webroot PATH] [--verbose]\n" +
        "  --no-tray     run without the notification-area icon (console/service mode)\n" +
        "  --no-browser  do not open the connection page (link, QR code, pairing code) at startup";

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {a}");
            switch (a.ToLowerInvariant())
            {
                case "--port":
                case "-p":
                    if (!int.TryParse(Next(), out int p) || p < 1 || p > 65535) throw new ArgumentException("Invalid port");
                    o.Port = p;
                    o.PortExplicit = true;
                    break;
                case "--no-tray":
                case "--headless":
                    o.NoTray = true;
                    break;
                case "--no-browser":
                    o.NoBrowser = true;
                    break;
                case "--verbose":
                case "-v":
                    o.Verbose = true;
                    break;
                case "--webroot":
                    o.WebRoot = Path.GetFullPath(Next());
                    break;
                case "--data-dir":
                    o.DataDir = Path.GetFullPath(Next());
                    break;
                case "--help":
                case "-h":
                case "/?":
                    throw new ArgumentException(Usage);
                default:
                    throw new ArgumentException($"Unknown argument '{a}'. Usage: {Usage}");
            }
        }
        return o;
    }
}
