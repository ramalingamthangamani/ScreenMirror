using ScreenMirror;

public class TrayAppTests
{
    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    [Fact]
    public void MenuCanBeBuiltAndRebuiltWithDevices()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ScreenMirrorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            RunSta(() =>
            {
                var server = new AppServer(Options.Parse(new[] { "--data-dir", dir, "--no-browser" }));
                // Regression: RefreshMenu used to dispose menu items while enumerating them and crash at startup.
                using var tray = new TrayApp(server);
                server.Pairing.TryPair(server.Pairing.Code, "Phone", out _, out var d);
                server.Pairing.TryPair(server.Pairing.Code, "Tablet", out _, out _);
                tray.RefreshMenu();
                tray.RefreshMenu();
                server.Pairing.Revoke(d!.Id);
                tray.RefreshMenu();
            });
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
