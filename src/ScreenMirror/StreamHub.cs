using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ScreenMirror;

public enum StreamQuality { Low, Balanced, High }

public readonly record struct QualityPreset(int MaxDim, int Jpeg, int Refine)
{
    public static QualityPreset For(StreamQuality q) => q switch
    {
        StreamQuality.Low => new QualityPreset(960, 50, 75),
        StreamQuality.High => new QualityPreset(1920, 75, 90),
        _ => new QualityPreset(1280, 62, 85),
    };

    public static bool TryParse(string? s, out StreamQuality q)
    {
        switch (s?.ToLowerInvariant())
        {
            case "low": q = StreamQuality.Low; return true;
            case "balanced": q = StreamQuality.Balanced; return true;
            case "high": q = StreamQuality.High; return true;
            default: q = StreamQuality.Balanced; return false;
        }
    }

    public static string Name(StreamQuality q) => q.ToString().ToLowerInvariant();
}

/// <summary>An encoded, ready-to-send binary frame (header + JPEG). Immutable once published.</summary>
public sealed class EncodedFrame
{
    /// <summary>
    /// Binary frame layout (little endian):
    ///  0 u8  type (1 = frame)        1 u8  flags (bit0 refined, bit1 cursor visible)
    ///  2 u16 monitor index           4 u32 frame id
    ///  8 f64 capture time (server Unix ms)
    /// 16 u16 width                  18 u16 height
    /// 20 f32 cursor x (normalized)  24 f32 cursor y (normalized)
    /// 28 ... JPEG bytes
    /// </summary>
    public const int HeaderSize = 28;

    public required long Id { get; init; }
    public required byte[] Data { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required bool Refined { get; init; }
}

/// <summary>Receives frames from a <see cref="StreamSource"/>. Implementations must not block.</summary>
public interface IFrameSink
{
    /// <summary>True when the sink can accept another frame right now (flow control).</summary>
    bool WantsFrame { get; }
    void OnFrame(EncodedFrame frame);
}

/// <summary>
/// Captures one monitor at one quality preset on a dedicated thread and fans frames out to all subscribed sinks.
/// Captures only when at least one sink can accept a frame, skips unchanged frames, and sends a sharper
/// refined frame once the screen has been static for a moment.
/// </summary>
public sealed class StreamSource
{
    private const double RefineAfterMs = 350;
    private const double IdleAfterMs = 1500;
    private const int IdlePollMs = 66;

    private readonly StreamHub _hub;
    private readonly object _gate = new();
    private readonly List<IFrameSink> _sinks = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _forceFrame = true;
    private volatile EncodedFrame? _last;
    private volatile MonitorInfo _monitor;

    public int MonitorIndex { get; }
    public StreamQuality Quality { get; }
    public MonitorInfo Monitor => _monitor;

    // Statistics (read by the dashboard).
    public double ProducedFps { get; private set; }
    public double AvgCaptureMs { get; private set; }
    public double AvgEncodeMs { get; private set; }
    public int LastFrameBytes { get; private set; }
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }
    public bool CaptureBlocked { get; private set; }

    internal StreamSource(StreamHub hub, int monitorIndex, StreamQuality quality)
    {
        _hub = hub;
        MonitorIndex = monitorIndex;
        Quality = quality;
        _monitor = Monitors.Get(monitorIndex);
    }

    public int SinkCount { get { lock (_gate) return _sinks.Count; } }

    internal void Add(IFrameSink sink)
    {
        lock (_gate) if (!_sinks.Contains(sink)) _sinks.Add(sink);
        var last = _last;
        if (last != null) sink.OnFrame(last);
        _forceFrame = true;
        Wake();
    }

    internal int Remove(IFrameSink sink)
    {
        lock (_gate)
        {
            _sinks.Remove(sink);
            return _sinks.Count;
        }
    }

    /// <summary>Wakes the capture thread (called on new demand, e.g. after a frame acknowledgement).</summary>
    public void Wake() => _wake.Set();

    /// <summary>Forces the next captured frame to be sent even if the screen did not change.</summary>
    public void RequestFrame()
    {
        _forceFrame = true;
        Wake();
    }

    internal void Start()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"Capture m{MonitorIndex} {Quality}",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    internal void Stop()
    {
        _stop = true;
        _wake.Set();
    }

    private bool AnySinkReady()
    {
        lock (_gate)
        {
            foreach (var s in _sinks)
                if (s.WantsFrame) return true;
        }
        return false;
    }

    private static (int w, int h) TargetSize(MonitorInfo mon, int maxDim)
    {
        int longSide = Math.Max(mon.Width, mon.Height);
        if (longSide <= maxDim) return (Math.Max(2, mon.Width & ~1), Math.Max(2, mon.Height & ~1));
        double s = (double)maxDim / longSide;
        int w = Math.Max(2, (int)Math.Round(mon.Width * s) & ~1);
        int h = Math.Max(2, (int)Math.Round(mon.Height * s) & ~1);
        return (w, h);
    }

    private void Run()
    {
        Native.timeBeginPeriod(1);
        var sw = Stopwatch.StartNew();
        var preset = QualityPreset.For(Quality);
        double nextMonitorCheck = 0, nextDue = 0, lastChange = 0, statsWindowStart = 0;
        bool refinedSent = true;
        int framesInWindow = 0;
        try
        {
            using var cap = new ScreenCapturer();
            while (!_stop)
            {
                if (!_hub.HostStreaming)
                {
                    _wake.WaitOne(250);
                    _forceFrame = true;
                    ProducedFps = 0;
                    continue;
                }

                double now = sw.Elapsed.TotalMilliseconds;
                if (now >= nextMonitorCheck)
                {
                    _monitor = Monitors.Get(MonitorIndex);
                    nextMonitorCheck = now + 1000;
                }
                if (now - statsWindowStart >= 1000)
                {
                    ProducedFps = framesInWindow * 1000.0 / Math.Max(1, now - statsWindowStart);
                    framesInWindow = 0;
                    statsWindowStart = now;
                }

                if (!AnySinkReady())
                {
                    _wake.WaitOne(100);
                    continue;
                }

                if (now < nextDue)
                {
                    Thread.Sleep(Math.Max(1, (int)(nextDue - now)));
                    continue;
                }
                int interval = 1000 / Math.Clamp(_hub.MaxFps, 1, 60);
                // Poll less often once the screen has been static for a while (saves CPU; the first change still shows within ~66 ms).
                if (refinedSent && now - lastChange > IdleAfterMs) interval = Math.Max(interval, IdlePollMs);
                nextDue = now + interval;

                var mon = _monitor;
                var (tw, th) = TargetSize(mon, preset.MaxDim);
                double t0 = sw.Elapsed.TotalMilliseconds;
                double captureTime = ServerClock.NowMs;
                if (!cap.Capture(mon, tw, th, drawCursor: true))
                {
                    if (!CaptureBlocked) { CaptureBlocked = true; _hub.NotifyStateChanged(); }
                    Thread.Sleep(250);
                    continue;
                }
                if (CaptureBlocked) { CaptureBlocked = false; _forceFrame = true; _hub.NotifyStateChanged(); }
                double t1 = sw.Elapsed.TotalMilliseconds;
                AvgCaptureMs = AvgCaptureMs * 0.9 + (t1 - t0) * 0.1;

                bool changed = cap.HasChanged();
                if (_forceFrame)
                {
                    _forceFrame = false;
                    changed = true;
                }

                if (changed)
                {
                    lastChange = now;
                    refinedSent = false;
                    Publish(cap, preset.Jpeg, captureTime, refined: false, sw);
                    framesInWindow++;
                }
                else if (!refinedSent && now - lastChange >= RefineAfterMs)
                {
                    refinedSent = true;
                    if (preset.Refine > preset.Jpeg)
                    {
                        Publish(cap, preset.Refine, captureTime, refined: true, sw);
                        framesInWindow++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Capture thread for monitor {MonitorIndex} crashed: {ex}");
        }
        finally
        {
            Native.timeEndPeriod(1);
        }
    }

    private void Publish(ScreenCapturer cap, int quality, double captureTime, bool refined, Stopwatch sw)
    {
        double e0 = sw.Elapsed.TotalMilliseconds;
        byte[] data = cap.Encode(quality, EncodedFrame.HeaderSize);
        AvgEncodeMs = AvgEncodeMs * 0.9 + (sw.Elapsed.TotalMilliseconds - e0) * 0.1;
        long id = _hub.NextFrameId();
        var span = data.AsSpan();
        span[0] = 1;
        span[1] = (byte)((refined ? 1 : 0) | (cap.CursorVisible ? 2 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)MonitorIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)id);
        BinaryPrimitives.WriteDoubleLittleEndian(span[8..], captureTime);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], (ushort)cap.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], (ushort)cap.Height);
        BinaryPrimitives.WriteSingleLittleEndian(span[20..], cap.CursorX);
        BinaryPrimitives.WriteSingleLittleEndian(span[24..], cap.CursorY);

        var frame = new EncodedFrame { Id = id, Data = data, Width = cap.Width, Height = cap.Height, Refined = refined };
        _last = frame;
        LastFrameBytes = data.Length;
        FrameWidth = cap.Width;
        FrameHeight = cap.Height;
        IFrameSink[] sinks;
        lock (_gate) sinks = _sinks.ToArray();
        foreach (var s in sinks)
        {
            try { s.OnFrame(frame); }
            catch (Exception ex) { Log.Error($"Frame sink failed: {ex.Message}"); }
        }
    }
}

/// <summary>Owns the stream sources. One source per (monitor, quality) pair, shared by every client that asks for it.</summary>
public sealed class StreamHub
{
    private readonly object _gate = new();
    private readonly Dictionary<(int, StreamQuality), StreamSource> _sources = new();
    private long _frameId;
    private volatile bool _hostStreaming = true;
    private volatile int _maxFps = 30;

    public event Action? StateChanged;

    public bool HostStreaming => _hostStreaming;
    public int MaxFps { get => _maxFps; set => _maxFps = Math.Clamp(value, 5, 60); }

    public long NextFrameId() => Interlocked.Increment(ref _frameId);

    public void SetHostStreaming(bool on)
    {
        if (_hostStreaming == on) return;
        _hostStreaming = on;
        foreach (var s in Sources) s.RequestFrame();
        NotifyStateChanged();
    }

    public void NotifyStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { Log.Error($"StateChanged handler failed: {ex.Message}"); }
    }

    public StreamSource[] Sources { get { lock (_gate) return _sources.Values.ToArray(); } }

    /// <summary>Moves <paramref name="sink"/> from <paramref name="current"/> (if any) to the source for (monitor, quality).</summary>
    public StreamSource Subscribe(IFrameSink sink, StreamSource? current, int monitor, StreamQuality quality)
    {
        int count = Monitors.GetAll().Count;
        if (monitor < 0 || monitor >= count) monitor = 0;
        lock (_gate)
        {
            if (current != null && current.MonitorIndex == monitor && current.Quality == quality
                && _sources.TryGetValue((monitor, quality), out var same) && same == current)
            {
                current.Add(sink);
                return current;
            }
            if (current != null) RemoveLocked(sink, current);
            if (!_sources.TryGetValue((monitor, quality), out var src))
            {
                src = new StreamSource(this, monitor, quality);
                _sources[(monitor, quality)] = src;
                src.Start();
            }
            src.Add(sink);
            return src;
        }
    }

    public void Unsubscribe(IFrameSink sink, StreamSource? current)
    {
        if (current == null) return;
        lock (_gate) RemoveLocked(sink, current);
    }

    private void RemoveLocked(IFrameSink sink, StreamSource src)
    {
        if (src.Remove(sink) == 0 && _sources.TryGetValue((src.MonitorIndex, src.Quality), out var existing) && existing == src)
        {
            _sources.Remove((src.MonitorIndex, src.Quality));
            src.Stop();
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var s in _sources.Values) s.Stop();
            _sources.Clear();
        }
    }
}
