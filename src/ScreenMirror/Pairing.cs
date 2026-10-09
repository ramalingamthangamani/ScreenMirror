using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenMirror;

/// <summary>A device that paired with this PC. Only a hash of its token is stored.</summary>
public sealed class PairedDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Device";
    public string TokenHash { get; set; } = "";
    public DateTime PairedAt { get; set; }
    public DateTime LastSeen { get; set; }
    public bool AllowControl { get; set; } = true;
}

public enum PairResult { Ok, WrongCode, Locked }

/// <summary>
/// Pairing: the PC shows a 6-digit code; a device that enters it receives a random token, saved (hashed) in
/// devices.json in the data directory, and reconnects with that token from then on. Each code pairs one device.
/// After <see cref="MaxFailures"/> wrong codes the code is replaced and pairing is locked for a short while.
/// </summary>
public sealed class Pairing
{
    public const int MaxFailures = 5;
    private static readonly TimeSpan LockTime = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _file;
    private readonly Dictionary<string, PairedDevice> _devices = new(StringComparer.Ordinal);
    private int _failures;
    private DateTime _lockedUntil = DateTime.MinValue;

    public Pairing(string dataDir)
    {
        _file = Path.Combine(dataDir, "devices.json");
        Code = NewCode();
        Load();
    }

    /// <summary>The code currently shown on the PC.</summary>
    public string Code { get; private set; }

    /// <summary>Raised when the code changes or devices are added/removed.</summary>
    public event Action? Changed;

    public PairedDevice[] Devices { get { lock (_gate) return _devices.Values.OrderBy(d => d.PairedAt).ToArray(); } }

    private static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public PairResult TryPair(string? code, string name, out string token, out PairedDevice? device)
    {
        token = "";
        device = null;
        lock (_gate)
        {
            if (DateTime.UtcNow < _lockedUntil) return PairResult.Locked;
            code = (code ?? "").Trim();
            bool ok = code.Length == Code.Length
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(Code));
            if (!ok)
            {
                if (++_failures >= MaxFailures)
                {
                    // Brute-force guard: retire the code and pause pairing.
                    _failures = 0;
                    _lockedUntil = DateTime.UtcNow + LockTime;
                    Code = NewCode();
                    Log.Warn($"Too many wrong pairing codes; pairing locked for {LockTime.TotalSeconds:0} s. New code: {Code}");
                    RaiseChangedLater();
                    return PairResult.Locked;
                }
                return PairResult.WrongCode;
            }
            _failures = 0;
            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var now = DateTime.UtcNow;
            device = new PairedDevice
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(),
                Name = name,
                TokenHash = Hash(token),
                PairedAt = now,
                LastSeen = now,
            };
            _devices[device.Id] = device;
            Code = NewCode(); // one code, one device
            SaveLocked();
            Log.Info($"Paired new device '{name}'. Next pairing code: {Code}");
        }
        RaiseChangedLater();
        return PairResult.Ok;
    }

    /// <summary>Returns the paired device that owns <paramref name="token"/>, or null.</summary>
    public PairedDevice? Find(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200) return null;
        byte[] hash = Encoding.ASCII.GetBytes(Hash(token));
        lock (_gate)
        {
            foreach (var d in _devices.Values)
                if (CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(d.TokenHash))) return d;
        }
        return null;
    }

    public void Touch(PairedDevice d, string? name = null)
    {
        lock (_gate)
        {
            d.LastSeen = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(name)) d.Name = name;
            SaveLocked();
        }
    }

    public void SetAllowControl(string id, bool allow)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(id, out var d) || d.AllowControl == allow) return;
            d.AllowControl = allow;
            SaveLocked();
        }
    }

    public bool Revoke(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _devices.Remove(id);
            if (removed) SaveLocked();
        }
        if (removed) RaiseChangedLater();
        return removed;
    }

    public void RevokeAll()
    {
        lock (_gate)
        {
            _devices.Clear();
            Code = NewCode();
            SaveLocked();
        }
        Log.Info($"All paired devices revoked. New pairing code: {Code}");
        RaiseChangedLater();
    }

    public void NewPairingCode()
    {
        lock (_gate)
        {
            Code = NewCode();
            _failures = 0;
        }
        Log.Info($"New pairing code: {Code}");
        RaiseChangedLater();
    }

    private void RaiseChangedLater()
    {
        // Never invoke handlers while holding the lock.
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Log.Error($"Pairing.Changed handler failed: {ex.Message}"); }
        });
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var list = JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(_file));
            if (list == null) return;
            foreach (var d in list)
                if (!string.IsNullOrEmpty(d.Id) && d.TokenHash.Length == 64)
                    _devices[d.Id] = d;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read {_file}: {ex.Message}");
        }
    }

    private void SaveLocked()
    {
        try
        {
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_devices.Values.ToList(), JsonOpts));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not save {_file}: {ex.Message}");
        }
    }
}
