using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ScreenMirror;

/// <summary>LAN address discovery and request host validation.</summary>
public static class Network
{
    /// <summary>IPv4 addresses phones on the LAN can reach, best first (interfaces with a default gateway, physical before virtual).</summary>
    public static List<IPAddress> LanAddresses()
    {
        var found = new List<(IPAddress ip, int score)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch (NetworkInformationException) { continue; }
                bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                string desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
                bool isVirtual = desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper-v") || desc.Contains("vethernet")
                    || desc.Contains("wsl") || desc.Contains("docker") || desc.Contains("vpn") || desc.Contains("tap-") || desc.Contains("loopback");
                foreach (var ua in props.UnicastAddresses)
                {
                    var ip = ua.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                    byte[] b = ip.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // link-local: no DHCP
                    int score = (gateway ? 4 : 0) + (isVirtual ? 0 : 2) + (IsPrivate(b) ? 1 : 0);
                    found.Add((ip, score));
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            Log.Warn($"Network enumeration failed: {ex.Message}");
        }
        return found.OrderByDescending(f => f.score).Select(f => f.ip).Distinct().ToList();
    }

    private static bool IsPrivate(byte[] b) =>
        b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);

    /// <summary>Phone-facing URLs, best first. Falls back to localhost when no LAN interface is up.</summary>
    public static List<string> Urls(int port)
    {
        var urls = LanAddresses().Select(ip => $"http://{ip}:{port}/").ToList();
        if (urls.Count == 0) urls.Add($"http://localhost:{port}/");
        return urls;
    }

    /// <summary>
    /// True when a request's Host header names this machine directly (IP literal, localhost or the computer name).
    /// Rejecting other names defeats DNS rebinding, where a web page on some domain re-points that domain to the PC.
    /// </summary>
    public static bool IsAllowedHost(string? hostHeader)
    {
        if (string.IsNullOrEmpty(hostHeader)) return false;
        string host = hostHeader;
        if (host.StartsWith('['))
        {
            int end = host.IndexOf(']');
            return end > 0 && IPAddress.TryParse(host.AsSpan(1, end - 1), out _);
        }
        int colon = host.LastIndexOf(':');
        if (colon >= 0) host = host[..colon];
        if (IPAddress.TryParse(host, out _)) return true;
        host = host.TrimEnd('.').ToLowerInvariant();
        if (host == "localhost") return true;
        string machine = Environment.MachineName.ToLowerInvariant();
        return host == machine || host.StartsWith(machine + ".", StringComparison.Ordinal);
    }

    /// <summary>True when a browser Origin header (if any) is this server itself, so other web sites cannot open the control socket.</summary>
    public static bool IsSameOrigin(string? origin, string? hostHeader)
    {
        if (string.IsNullOrEmpty(origin)) return true; // not a browser (or a same-origin request from an old browser)
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return string.Equals(uri.Authority, hostHeader, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host + ":" + uri.Port, hostHeader, StringComparison.OrdinalIgnoreCase);
    }
}
