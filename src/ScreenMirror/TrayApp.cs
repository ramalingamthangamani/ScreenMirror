using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace ScreenMirror;

/// <summary>Notification-area icon and menu: the PC-side control surface when running normally (not --no-tray).</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly AppServer _server;
    private readonly NotifyIcon _icon;
    private readonly Icon _appIcon;
    private readonly Control _ui; // marshals server events onto the UI thread
    private readonly ToolStripMenuItem _miHeader, _miCode, _miPause, _miControl, _miDevices, _miRevokeAll;
    private string[] _connected = Array.Empty<string>();

    public TrayApp(AppServer server)
    {
        _server = server;
        _ui = new Control();
        _ui.CreateControl();
        _appIcon = CreateIcon();

        var menu = new ContextMenuStrip();
        _miHeader = new ToolStripMenuItem($"ScreenMirror on {Environment.MachineName}") { Enabled = false };
        _miCode = new ToolStripMenuItem("Pairing code", null, (_, _) => ShowPairingInfo());
        _miPause = new ToolStripMenuItem("Pause streaming", null, (_, _) => _server.SetHostStreaming(!_server.Hub.HostStreaming));
        _miControl = new ToolStripMenuItem("Allow remote control", null, (_, _) => _server.InputEnabled = !_server.InputEnabled);
        _miDevices = new ToolStripMenuItem("Paired devices");
        _miDevices.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
        _miRevokeAll = new ToolStripMenuItem("Revoke all devices…", null, (_, _) => RevokeAll());

        menu.Items.AddRange(new ToolStripItem[]
        {
            _miHeader,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open in browser", null, (_, _) => OpenBrowser(_server.ConnectPageUrl)) { Font = new Font(menu.Font, FontStyle.Bold) },
            new ToolStripMenuItem("Show pairing code / link…", null, (_, _) => ShowPairingInfo()),
            _miCode,
            new ToolStripMenuItem("New pairing code", null, (_, _) => _server.Pairing.NewPairingCode()),
            new ToolStripSeparator(),
            _miPause,
            _miControl,
            new ToolStripSeparator(),
            _miDevices,
            _miRevokeAll,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open data folder", null, (_, _) => OpenBrowser(_server.Options.DataDir)),
            new ToolStripMenuItem("Quit", null, (_, _) => ExitThread()),
        });
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new NotifyIcon
        {
            Icon = _appIcon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenBrowser(_server.ConnectPageUrl);
        _icon.BalloonTipClicked += (_, _) => OpenBrowser(_server.ConnectPageUrl);

        _server.SessionsChanged += () => Post(OnSessionsChanged);
        _server.Pairing.Changed += () => Post(RefreshMenu);
        RefreshMenu();
    }

    public static void OpenBrowser(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {target}: {ex.Message}");
        }
    }

    private void Post(Action a)
    {
        try
        {
            if (!_ui.IsDisposed) _ui.BeginInvoke(a);
        }
        catch (InvalidOperationException) { /* shutting down */ }
    }

    private void OnSessionsChanged()
    {
        var now = _server.Sessions.Values.Where(s => s.Authorized).Select(s => s.Name).OrderBy(n => n).ToArray();
        var joined = now.Except(_connected).ToArray();
        _connected = now;
        if (joined.Length > 0)
            _icon.ShowBalloonTip(3000, "Device connected", $"{string.Join(", ", joined)} is viewing this PC.", ToolTipIcon.Info);
        RefreshMenu();
    }

    internal void RefreshMenu()
    {
        string code = _server.Pairing.Code;
        int connected = _server.Sessions.Values.Count(s => s.Authorized);
        _miHeader.Text = $"ScreenMirror  ·  {_server.PrimaryUrl}";
        _miCode.Text = $"Pairing code: {code[..3]} {code[3..]}";
        _miPause.Checked = !_server.Hub.HostStreaming;
        _miControl.Checked = _server.InputEnabled;

        string tip = $"ScreenMirror · code {code}\n{connected} device{(connected == 1 ? "" : "s")} connected";
        if (!_server.Hub.HostStreaming) tip += " · paused";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;

        // Disposing an item removes it from the collection, so take a copy before iterating.
        var oldItems = _miDevices.DropDownItems.Cast<ToolStripItem>().ToArray();
        _miDevices.DropDownItems.Clear();
        foreach (var old in oldItems) old.Dispose();
        var devices = _server.Pairing.Devices;
        if (devices.Length == 0)
            _miDevices.DropDownItems.Add(new ToolStripMenuItem("(none)") { Enabled = false });
        foreach (var d in devices)
        {
            string id = d.Id;
            bool online = _server.Sessions.Values.Any(s => s.Authorized && s.Device?.Id == id);
            var item = new ToolStripMenuItem(d.Name + (online ? "  (connected)" : ""));
            item.DropDownItems.Add(new ToolStripMenuItem("Allow control", null, (_, _) => _server.SetDeviceControl(id, !d.AllowControl)) { Checked = d.AllowControl });
            item.DropDownItems.Add(new ToolStripMenuItem("Remove device", null, (_, _) => _server.RevokeDevice(id)));
            _miDevices.DropDownItems.Add(item);
        }
        _miRevokeAll.Enabled = devices.Length > 0;
    }

    private void ShowPairingInfo()
    {
        string code = _server.Pairing.Code;
        var urls = Network.Urls(_server.Port);
        string link = _server.PairingUrl(urls[0]);
        try { Clipboard.SetText(link); } catch (Exception ex) { Log.Debug($"Clipboard: {ex.Message}"); }
        string others = urls.Count > 1 ? "\n\nOther addresses of this PC:\n" + string.Join("\n", urls.Skip(1)) : "";
        MessageBox.Show(
            $"On your phone or tablet (same Wi-Fi), open:\n\n{urls[0]}\n\nand enter the pairing code:  {code}\n\n" +
            $"The direct pairing link was copied to the clipboard. \"Open in browser\" shows a QR code to scan.{others}",
            "ScreenMirror - connect a device", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RevokeAll()
    {
        var r = MessageBox.Show("Remove all paired devices? They will be disconnected and need a new pairing code.",
            "ScreenMirror", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r == DialogResult.OK) _server.RevokeAll();
    }

    /// <summary>Draws the tray icon (a screen with a phone in front) so no binary resource is needed.</summary>
    private static Icon CreateIcon()
    {
        int size = SystemInformation.SmallIconSize.Width >= 24 ? 32 : 16;
        size = Math.Max(size, 32);
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float s = size / 32f;
            using var monitor = RoundedRect(new RectangleF(1 * s, 4 * s, 26 * s, 18 * s), 3 * s);
            using var monitorBrush = new LinearGradientBrush(new PointF(0, 0), new PointF(size, size), Color.FromArgb(79, 140, 255), Color.FromArgb(124, 92, 255));
            g.FillPath(monitorBrush, monitor);
            using var stand = new SolidBrush(Color.FromArgb(200, 210, 230));
            g.FillRectangle(stand, 10 * s, 23 * s, 8 * s, 2.5f * s);
            using var phone = RoundedRect(new RectangleF(19 * s, 11 * s, 12 * s, 20 * s), 2.5f * s);
            using var phoneFill = new SolidBrush(Color.FromArgb(20, 24, 33));
            using var phonePen = new Pen(Color.White, 2 * s);
            g.FillPath(phoneFill, phone);
            g.DrawPath(phonePen, phone);
        }
        IntPtr h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        var icon = (Icon)tmp.Clone();
        Native.DestroyIcon(h);
        return icon;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _appIcon.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
