using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ScreenMirror;

/// <summary>Hosts the HTTP/WebSocket server and owns the stream hub, pairing store and connected sessions.</summary>
public sealed class AppServer : IAsyncDisposable
{
    private WebApplication? _app;
    private Timer? _statsTimer;
    private volatile bool _inputEnabled = true;

    public AppServer(Options options)
    {
        Options = options;
        Pairing = new Pairing(options.DataDir);
        Hub.StateChanged += BroadcastState;
    }

    public Options Options { get; }
    public StreamHub Hub { get; } = new();
    public Pairing Pairing { get; }
    public ConcurrentDictionary<int, ClientSession> Sessions { get; } = new();
    public int Port { get; private set; }

    /// <summary>Raised (on a worker thread) when a client connects, disconnects or changes what it streams.</summary>
    public event Action? SessionsChanged;

    /// <summary>Global switch (tray menu): when false no device may inject input.</summary>
    public bool InputEnabled
    {
        get => _inputEnabled;
        set
        {
            if (_inputEnabled == value) return;
            _inputEnabled = value;
            Log.Info(value ? "Remote control allowed" : "Remote control blocked");
            foreach (var s in Sessions.Values) s.OnControlChanged();
            NotifySessionsChanged();
        }
    }

    /// <summary>The address phones should open (first LAN address).</summary>
    public string PrimaryUrl => Network.Urls(Port)[0];

    /// <summary>Local page showing the link, QR code and pairing code.</summary>
    public string ConnectPageUrl => $"http://localhost:{Port}/connect";

    public async Task StartAsync()
    {
        int first = Options.Port;
        int attempts = Options.PortExplicit ? 1 : 10;
        Exception? last = null;
        for (int port = first; port < first + attempts; port++)
        {
            var app = Build(port);
            try
            {
                await app.StartAsync();
                _app = app;
                Port = port;
                _statsTimer = new Timer(_ => Tick(), null, 1000, 1000);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Log.Warn($"Port {port} unavailable: {ex.Message}");
                await app.DisposeAsync();
            }
        }
        throw new InvalidOperationException($"Could not listen on port {first}{(attempts > 1 ? $"-{first + attempts - 1}" : "")}.", last);
    }

    private WebApplication Build(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 64 * 1024;
            k.ListenAnyIP(port);
        });

        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            // Only answer requests addressed to this PC by IP or computer name (defeats DNS rebinding).
            if (!Network.IsAllowedHost(ctx.Request.Host.Value))
            {
                ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }
            await next(ctx);
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });

        app.Map("/ws", HandleClientSocketAsync);
        app.MapPost("/api/pair", HandlePairAsync);
        app.MapGet("/api/info", ServeInfoAsync);
        app.MapGet("/qr.svg", ServeQrAsync);
        app.MapGet("/connect", ctx => IsLocal(ctx) ? StaticAssets.ServeAsync(ctx, "connect.html", Options.WebRoot) : Forbidden(ctx));
        app.MapGet("/", ctx => StaticAssets.ServeAsync(ctx, "index.html", Options.WebRoot));
        app.MapGet("/{file}", (HttpContext ctx, string file) => StaticAssets.ServeAsync(ctx, file, Options.WebRoot));
        return app;
    }

    private static bool IsLocal(HttpContext ctx) => ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    private static Task Forbidden(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return ctx.Response.WriteAsync("Only available on the PC itself.");
    }

    private static Task WriteJsonAsync(HttpContext ctx, object value, int status = 200)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(value));
    }

    /// <summary>POST /api/pair {code, name} -> {token, deviceId} | 403 wrong code | 429 locked.</summary>
    private async Task HandlePairAsync(HttpContext ctx)
    {
        if (!Network.IsSameOrigin(ctx.Request.Headers.Origin, ctx.Request.Host.Value))
        {
            await Forbidden(ctx);
            return;
        }
        string? code = null, name = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                code = doc.RootElement.Str("code");
                name = doc.RootElement.Str("name");
            }
        }
        catch (JsonException) { }

        string clean = ClientSession.Sanitize(name, 40) is { Length: > 0 } n ? n : "Device";
        string remote = RemoteAddress(ctx);
        switch (Pairing.TryPair(code, clean, out string token, out var device))
        {
            case PairResult.Ok:
                Log.Info($"Device '{clean}' paired from {remote}");
                await WriteJsonAsync(ctx, new { token, deviceId = device!.Id });
                break;
            case PairResult.Locked:
                Log.Warn($"Pairing attempt from {remote} refused (locked)");
                await WriteJsonAsync(ctx, new { error = "Too many wrong codes. Wait 30 seconds and use the new code shown on the PC." }, StatusCodes.Status429TooManyRequests);
                break;
            default:
                Log.Warn($"Wrong pairing code from {remote}");
                await WriteJsonAsync(ctx, new { error = "That code is not valid. Check the code shown on your PC." }, StatusCodes.Status403Forbidden);
                break;
        }
    }

    /// <summary>GET /api/info: links for everyone; pairing code and device list only on the PC itself.</summary>
    private Task ServeInfoAsync(HttpContext ctx)
    {
        bool local = IsLocal(ctx);
        object? devices = null;
        if (local)
        {
            devices = Pairing.Devices.Select(d =>
            {
                var live = Sessions.Values.FirstOrDefault(s => s.Device?.Id == d.Id);
                return new
                {
                    id = d.Id,
                    name = live?.Name ?? d.Name,
                    connected = live != null,
                    address = live?.RemoteAddress,
                    streaming = live?.Streaming ?? false,
                    control = d.AllowControl,
                    fps = live != null ? Math.Round(live.ClientFps, 1) : 0,
                    latency = live != null ? Math.Round(live.ClientLatencyMs) : 0,
                    kbps = live != null ? Math.Round(live.SendKbps) : 0,
                    lastSeen = d.LastSeen,
                };
            }).ToArray();
        }
        return WriteJsonAsync(ctx, new
        {
            host = Environment.MachineName,
            port = Port,
            urls = Network.Urls(Port),
            code = local ? Pairing.Code : null,
            hostStreaming = Hub.HostStreaming,
            inputEnabled = InputEnabled,
            devices,
        });
    }

    /// <summary>GET /qr.svg?i=N: QR code for link N, carrying the pairing code (PC only).</summary>
    private Task ServeQrAsync(HttpContext ctx)
    {
        if (!IsLocal(ctx)) return Forbidden(ctx);
        var urls = Network.Urls(Port);
        int i = int.TryParse(ctx.Request.Query["i"], out int n) && n >= 0 && n < urls.Count ? n : 0;
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.ContentType = "image/svg+xml";
        return ctx.Response.WriteAsync(QrCode.Encode(PairingUrl(urls[i])).ToSvg());
    }

    /// <summary>A link that pairs automatically when opened (the code is single use).</summary>
    public string PairingUrl(string baseUrl) => $"{baseUrl}?code={Pairing.Code}";

    private static string RemoteAddress(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : "?";

    private async Task HandleClientSocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        if (!Network.IsSameOrigin(ctx.Request.Headers.Origin, ctx.Request.Host.Value))
        {
            // Another web site open in a browser on the LAN must not be able to drive this PC.
            Log.Warn($"Rejected WebSocket from foreign origin {ctx.Request.Headers.Origin}");
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        string remote = RemoteAddress(ctx);
        var session = new ClientSession(ws, this, remote);
        Sessions[session.Id] = session;
        Log.Debug($"Client {session.Id} connected from {remote}");
        try
        {
            await session.RunAsync(ctx.RequestAborted);
        }
        finally
        {
            Sessions.TryRemove(session.Id, out _);
            if (session.Authorized) Log.Info($"Client {session.Id} ({session.Name}) disconnected");
            NotifySessionsChanged();
        }
    }

    public void NotifySessionsChanged()
    {
        try { SessionsChanged?.Invoke(); }
        catch (Exception ex) { Log.Error($"SessionsChanged handler failed: {ex.Message}"); }
    }

    /// <summary>Pushes current stream state to every connected client (host pause/resume, capture blocked, etc.).</summary>
    public void BroadcastState()
    {
        foreach (var s in Sessions.Values)
            if (s.Authorized) _ = s.SendStateAsync();
        NotifySessionsChanged();
    }

    public void SetHostStreaming(bool on) => Hub.SetHostStreaming(on);

    public void SetDeviceControl(string deviceId, bool allow)
    {
        Pairing.SetAllowControl(deviceId, allow);
        foreach (var s in Sessions.Values)
            if (s.Device?.Id == deviceId) s.OnControlChanged();
        NotifySessionsChanged();
    }

    /// <summary>Forgets one device and disconnects it; it has to pair again.</summary>
    public void RevokeDevice(string deviceId)
    {
        Pairing.Revoke(deviceId);
        foreach (var s in Sessions.Values)
            if (s.Device?.Id == deviceId) s.Kick("This device was removed on the PC. Pair again to reconnect.", reconnect: false, unpaired: true);
        NotifySessionsChanged();
    }

    /// <summary>Forgets every paired device and disconnects them all.</summary>
    public void RevokeAll()
    {
        Pairing.RevokeAll();
        foreach (var s in Sessions.Values)
            s.Kick("All devices were removed on the PC. Pair again to reconnect.", reconnect: false, unpaired: true);
        NotifySessionsChanged();
    }

    private void Tick()
    {
        foreach (var s in Sessions.Values) s.UpdateRates();
    }

    public async ValueTask DisposeAsync()
    {
        _statsTimer?.Dispose();
        foreach (var s in Sessions.Values) s.Kick("The PC stopped sharing its screen.", reconnect: true);
        Hub.StopAll();
        if (_app != null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _app.StopAsync(cts.Token);
            }
            catch { /* best effort */ }
            await _app.DisposeAsync();
        }
    }
}
