using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace ScreenMirror;

/// <summary>A physical monitor in virtual-desktop pixel coordinates (process is per-monitor DPI aware).</summary>
public sealed record MonitorInfo(int Index, string Device, int X, int Y, int Width, int Height, bool Primary)
{
    public string Label => $"Display {Index + 1}{(Primary ? " (main)" : "")} - {Width}x{Height}";
}

public static class Monitors
{
    /// <summary>Enumerates monitors. Primary first, then left-to-right, top-to-bottom. Index is the position in that order.</summary>
    public static List<MonitorInfo> GetAll()
    {
        var raw = new List<(string dev, Native.RECT r, bool primary)>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Native.RECT rc, IntPtr d) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfoW(h, ref mi))
                raw.Add((mi.szDevice ?? "", mi.rcMonitor, (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);

        var ordered = raw.OrderByDescending(m => m.primary).ThenBy(m => m.r.Left).ThenBy(m => m.r.Top).ToList();
        var list = new List<MonitorInfo>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            var (dev, r, primary) = ordered[i];
            list.Add(new MonitorInfo(i, dev, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, primary));
        }
        if (list.Count == 0)
        {
            // Should never happen on an interactive desktop; fall back to the virtual screen.
            list.Add(new MonitorInfo(0, "DISPLAY", Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
                Math.Max(1, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN)), Math.Max(1, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN)), true));
        }
        return list;
    }

    public static MonitorInfo Get(int index)
    {
        var all = GetAll();
        return index >= 0 && index < all.Count ? all[index] : all[0];
    }
}

/// <summary>Monotonic high resolution clock expressed as Unix epoch milliseconds (used for latency measurement).</summary>
public static class ServerClock
{
    private static readonly double BaseUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    public static double NowMs => BaseUnixMs + Sw.Elapsed.TotalMilliseconds;
}
