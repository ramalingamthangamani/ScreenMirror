using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ScreenMirror;

/// <summary>
/// Captures a monitor region into a reusable 32bpp DIB (optionally downscaled with HALFTONE filtering),
/// draws the mouse cursor into it, detects changes against the previous capture and encodes JPEG.
/// Not thread-safe: one instance per capture thread.
/// </summary>
internal sealed unsafe class ScreenCapturer : IDisposable
{
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private IntPtr _memDc, _dib, _oldBmp, _bits;
    private int _w, _h;
    private byte[] _prev = Array.Empty<byte>();
    private bool _hasPrev;
    private readonly MemoryStream _ms = new(512 * 1024);

    public int Width => _w;
    public int Height => _h;
    /// <summary>Cursor hotspot position normalized to the captured monitor (may be outside 0..1).</summary>
    public float CursorX { get; private set; }
    public float CursorY { get; private set; }
    public bool CursorVisible { get; private set; }

    private void Ensure(int w, int h)
    {
        if (w == _w && h == _h && _dib != IntPtr.Zero) return;
        FreeDib();
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        try
        {
            if (_memDc == IntPtr.Zero)
            {
                _memDc = Native.CreateCompatibleDC(screen);
                if (_memDc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed");
            }
            var bih = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            };
            _dib = Native.CreateDIBSection(screen, ref bih, Native.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero || _bits == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
            _oldBmp = Native.SelectObject(_memDc, _dib);
            Native.SetStretchBltMode(_memDc, Native.HALFTONE);
            Native.SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
            _w = w;
            _h = h;
            _prev = new byte[w * h * 4];
            _hasPrev = false;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Captures <paramref name="mon"/> scaled to tw x th. Returns false when the desktop cannot be read (e.g. UAC secure desktop, lock screen).</summary>
    public bool Capture(MonitorInfo mon, int tw, int th, bool drawCursor)
    {
        Ensure(tw, th);
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return false;
        bool ok;
        try
        {
            ok = tw == mon.Width && th == mon.Height
                ? Native.BitBlt(_memDc, 0, 0, tw, th, screen, mon.X, mon.Y, Native.SRCCOPY)
                : Native.StretchBlt(_memDc, 0, 0, tw, th, screen, mon.X, mon.Y, mon.Width, mon.Height, Native.SRCCOPY);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
        if (!ok) return false;
        CursorVisible = false;
        if (drawCursor) DrawCursor(mon, tw, th);
        return true;
    }

    private void DrawCursor(MonitorInfo mon, int tw, int th)
    {
        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci)) return;
        CursorX = (ci.ptScreenPos.X - mon.X + 0.5f) / mon.Width;
        CursorY = (ci.ptScreenPos.Y - mon.Y + 0.5f) / mon.Height;
        if ((ci.flags & Native.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
        if (!Native.GetIconInfo(ci.hCursor, out var ii)) return;
        try
        {
            int cw = 32, ch = 32;
            if (ii.hbmColor != IntPtr.Zero && Native.GetObjectW(ii.hbmColor, Marshal.SizeOf<Native.BITMAP>(), out var bmc) != 0)
            {
                cw = bmc.bmWidth;
                ch = bmc.bmHeight;
            }
            else if (ii.hbmMask != IntPtr.Zero && Native.GetObjectW(ii.hbmMask, Marshal.SizeOf<Native.BITMAP>(), out var bmm) != 0)
            {
                cw = bmm.bmWidth;
                ch = bmm.bmHeight / 2; // monochrome cursors stack AND and XOR masks vertically
            }
            double sx = (double)tw / mon.Width, sy = (double)th / mon.Height;
            int x = (int)Math.Round((ci.ptScreenPos.X - mon.X - ii.xHotspot) * sx);
            int y = (int)Math.Round((ci.ptScreenPos.Y - mon.Y - ii.yHotspot) * sy);
            int dw = Math.Max(1, (int)Math.Round(cw * sx));
            int dh = Math.Max(1, (int)Math.Round(ch * sy));
            if (x + dw <= 0 || y + dh <= 0 || x >= tw || y >= th) return; // cursor is on another monitor
            if (Native.DrawIconEx(_memDc, x, y, ci.hCursor, dw, dh, 0, IntPtr.Zero, Native.DI_NORMAL))
                CursorVisible = true;
        }
        finally
        {
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
        }
    }

    /// <summary>True when the last capture differs from the previous one (first capture always counts as changed).</summary>
    public bool HasChanged()
    {
        var cur = new ReadOnlySpan<byte>((void*)_bits, _w * _h * 4);
        if (_hasPrev && cur.SequenceEqual(_prev)) return false;
        cur.CopyTo(_prev);
        _hasPrev = true;
        return true;
    }

    /// <summary>Encodes the last capture as JPEG, prefixed by <paramref name="headerSize"/> zero bytes reserved for the frame header.</summary>
    public byte[] Encode(int quality, int headerSize)
    {
        _ms.SetLength(headerSize);
        _ms.Position = headerSize;
        using (var bmp = new Bitmap(_w, _h, _w * 4, PixelFormat.Format32bppRgb, _bits))
        using (var ep = new EncoderParameters(1))
        {
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 10, 100));
            bmp.Save(_ms, JpegCodec, ep);
        }
        return _ms.ToArray();
    }

    private void FreeDib()
    {
        if (_dib == IntPtr.Zero) return;
        if (_memDc != IntPtr.Zero && _oldBmp != IntPtr.Zero) Native.SelectObject(_memDc, _oldBmp);
        Native.DeleteObject(_dib);
        _dib = IntPtr.Zero;
        _bits = IntPtr.Zero;
        _oldBmp = IntPtr.Zero;
        _w = _h = 0;
    }

    public void Dispose()
    {
        FreeDib();
        if (_memDc != IntPtr.Zero)
        {
            Native.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }
        _ms.Dispose();
    }
}
