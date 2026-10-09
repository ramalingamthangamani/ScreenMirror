using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace ScreenMirror;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (options.NoTray) Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);

        // Must happen before any window or capture: coordinates everywhere are physical pixels.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Log.Init(options.DataDir, options.Verbose);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"Unhandled exception: {e.ExceptionObject}");
        Application.ThreadException += (_, e) =>
        {
            Log.Error($"UI thread exception: {e.Exception}");
            MessageBox.Show(e.Exception.Message, "ScreenMirror", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        // One tray instance per data directory; a second launch just reopens the running one's page.
        string portFile = Path.Combine(options.DataDir, "port.txt");
        using var instance = new Mutex(true, @"Local\ScreenMirror." + StableHash(options.DataDir), out bool firstInstance);
        if (!firstInstance)
        {
            Log.Info("ScreenMirror is already running");
            if (!options.NoBrowser && TryReadPort(portFile, out int running)) TrayApp.OpenBrowser($"http://localhost:{running}/connect");
            else if (!options.NoTray) MessageBox.Show("ScreenMirror is already running. Use its icon in the notification area.", "ScreenMirror");
            return 0;
        }

        var server = new AppServer(options);
        try
        {
            server.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex.ToString());
            if (!options.NoTray)
                MessageBox.Show(ex.Message, "ScreenMirror", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        try { File.WriteAllText(portFile, server.Port.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Log.Info($"ScreenMirror listening on port {server.Port}");
        foreach (string url in Network.Urls(server.Port)) Log.Info($"Open on your phone: {url}");
        Log.Info($"Pairing code: {server.Pairing.Code}  (connection page with QR code: {server.ConnectPageUrl})");
        if (!options.NoBrowser) TrayApp.OpenBrowser(server.ConnectPageUrl);

        if (options.NoTray)
        {
            using var exit = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { exit.Set(); } catch (ObjectDisposedException) { } };
            exit.Wait();
        }
        else
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var tray = new TrayApp(server);
            Application.Run(tray);
        }

        server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        try { File.Delete(portFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Log.Info("ScreenMirror stopped");
        return 0;
    }

    private static bool TryReadPort(string file, out int port)
    {
        port = 0;
        try { return int.TryParse(File.ReadAllText(file).Trim(), out port) && port > 0 && port < 65536; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s.ToLowerInvariant()) h = (h ^ c) * 16777619;
        return h.ToString("x8");
    }
}
