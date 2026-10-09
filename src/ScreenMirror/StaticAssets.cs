using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace ScreenMirror;

/// <summary>Serves the web UI from embedded resources (or from --webroot during development).</summary>
public static partial class StaticAssets
{
    private static readonly Assembly Asm = typeof(StaticAssets).Assembly;
    private static readonly ConcurrentDictionary<string, (byte[] Data, string ETag)?> Cache = new();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$")]
    private static partial Regex SafeName();

    public static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".json" => "application/json",
        ".webmanifest" => "application/manifest+json",
        _ => "application/octet-stream",
    };

    public static bool TryGet(string name, string? webRoot, out byte[] data, out string etag)
    {
        data = Array.Empty<byte>();
        etag = "";
        if (!SafeName().IsMatch(name) || name.Contains("..", StringComparison.Ordinal)) return false;

        if (webRoot != null)
        {
            string path = Path.Combine(webRoot, name);
            if (!File.Exists(path)) return false;
            data = File.ReadAllBytes(path);
            etag = MakeETag(data);
            return true;
        }

        var entry = Cache.GetOrAdd(name, static n =>
        {
            using var s = Asm.GetManifestResourceStream("wwwroot/" + n);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            return (bytes, MakeETag(bytes));
        });
        if (entry == null) return false;
        (data, etag) = entry.Value;
        return true;
    }

    private static string MakeETag(byte[] data) => "\"" + Convert.ToHexString(SHA256.HashData(data), 0, 8) + "\"";

    public static async Task ServeAsync(HttpContext ctx, string name, string? webRoot)
    {
        if (!TryGet(name, webRoot, out var data, out var etag))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var h = ctx.Response.Headers;
        h.ETag = etag;
        h.CacheControl = "no-cache";
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            h["X-Frame-Options"] = "DENY";
            // Older Safari versions do not let 'self' cover ws:, so name the socket origin explicitly.
            string host = ctx.Request.Host.HasValue ? ctx.Request.Host.Value! : "";
            string ws = host.Length > 0 && host.IndexOfAny(new[] { ' ', ';', ',', '\'', '"' }) < 0 ? $" ws://{host} wss://{host}" : "";
            h["Content-Security-Policy"] =
                $"default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; connect-src 'self'{ws}; script-src 'self'; frame-ancestors 'none'";
        }
        if (ctx.Request.Headers.IfNoneMatch == etag)
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }
        ctx.Response.ContentType = ContentType(name);
        ctx.Response.ContentLength = data.Length;
        await ctx.Response.Body.WriteAsync(data);
    }
}
