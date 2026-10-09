using System.Text.Json;
using ScreenMirror;

public sealed class PairingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenMirrorTests", Guid.NewGuid().ToString("N"));

    public PairingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private Pairing Create() => new(_dir);

    [Fact]
    public void CodeIsSixDigits()
    {
        var p = Create();
        Assert.Matches("^[0-9]{6}$", p.Code);
        Assert.Empty(p.Devices);
    }

    [Fact]
    public void CorrectCodePairsAndRotatesCode()
    {
        var p = Create();
        string code = p.Code;
        var r = p.TryPair(code, "Phone", out string token, out var device);
        Assert.Equal(PairResult.Ok, r);
        Assert.NotNull(device);
        Assert.Equal("Phone", device!.Name);
        Assert.True(device.AllowControl);
        Assert.True(token.Length >= 40);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.NotEqual(code, p.Code); // one code pairs one device
        Assert.Single(p.Devices);

        // The same code must not work twice.
        Assert.Equal(PairResult.WrongCode, p.TryPair(code, "Again", out _, out _));
    }

    [Fact]
    public void CodeIsTrimmedAndNullIsWrong()
    {
        var p = Create();
        Assert.Equal(PairResult.WrongCode, p.TryPair(null, "x", out _, out _));
        Assert.Equal(PairResult.WrongCode, p.TryPair("", "x", out _, out _));
        Assert.Equal(PairResult.Ok, p.TryPair("  " + p.Code + " ", "x", out _, out _));
    }

    [Fact]
    public void FindReturnsOwnerOfTokenOnly()
    {
        var p = Create();
        p.TryPair(p.Code, "A", out string ta, out var da);
        p.TryPair(p.Code, "B", out string tb, out var db);
        Assert.Same(da, p.Find(ta));
        Assert.Same(db, p.Find(tb));
        Assert.Null(p.Find(null));
        Assert.Null(p.Find(""));
        Assert.Null(p.Find("not-a-token"));
        Assert.Null(p.Find(new string('a', 300)));
    }

    [Fact]
    public void TooManyWrongCodesLocksAndRotates()
    {
        var p = Create();
        string original = p.Code;
        for (int i = 0; i < Pairing.MaxFailures - 1; i++)
            Assert.Equal(PairResult.WrongCode, p.TryPair("000000", "x", out _, out _));
        Assert.Equal(original, p.Code);
        Assert.Equal(PairResult.Locked, p.TryPair("000000", "x", out _, out _));
        Assert.NotEqual(original, p.Code);
        // Even the right code is refused while locked.
        Assert.Equal(PairResult.Locked, p.TryPair(p.Code, "x", out _, out _));
        Assert.Empty(p.Devices);
    }

    [Fact]
    public void NewPairingCodeResetsFailureCount()
    {
        var p = Create();
        for (int i = 0; i < Pairing.MaxFailures - 1; i++) p.TryPair("000000", "x", out _, out _);
        p.NewPairingCode();
        // One more wrong code would have locked without the reset.
        Assert.Equal(PairResult.WrongCode, p.TryPair("000000", "x", out _, out _));
        Assert.Equal(PairResult.Ok, p.TryPair(p.Code, "x", out _, out _));
    }

    [Fact]
    public void DevicesPersistAcrossInstancesWithHashedTokensOnly()
    {
        var p1 = Create();
        p1.TryPair(p1.Code, "Tablet", out string token, out var d1);
        p1.SetAllowControl(d1!.Id, false);

        string json = File.ReadAllText(Path.Combine(_dir, "devices.json"));
        Assert.DoesNotContain(token, json);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        var p2 = Create();
        var d2 = p2.Find(token);
        Assert.NotNull(d2);
        Assert.Equal(d1.Id, d2!.Id);
        Assert.Equal("Tablet", d2.Name);
        Assert.False(d2.AllowControl);
        Assert.NotEqual(p1.Code, p2.Code); // a fresh code per run
    }

    [Fact]
    public void CorruptFileIsIgnored()
    {
        File.WriteAllText(Path.Combine(_dir, "devices.json"), "{ not json");
        var p = Create();
        Assert.Empty(p.Devices);
        Assert.Equal(PairResult.Ok, p.TryPair(p.Code, "x", out _, out _));
    }

    [Fact]
    public void RevokeForgetsDevice()
    {
        var p = Create();
        p.TryPair(p.Code, "A", out string ta, out var da);
        p.TryPair(p.Code, "B", out string tb, out _);
        Assert.True(p.Revoke(da!.Id));
        Assert.False(p.Revoke(da.Id));
        Assert.Null(p.Find(ta));
        Assert.NotNull(p.Find(tb));
        Assert.Single(p.Devices);

        string code = p.Code;
        p.RevokeAll();
        Assert.Empty(p.Devices);
        Assert.Null(p.Find(tb));
        Assert.NotEqual(code, p.Code);
        Assert.Empty(Create().Devices); // persisted
    }

    [Fact]
    public void TouchUpdatesLastSeenAndName()
    {
        var p = Create();
        p.TryPair(p.Code, "Old", out _, out var d);
        var before = d!.LastSeen;
        Thread.Sleep(5);
        p.Touch(d, "New");
        Assert.True(d.LastSeen > before);
        Assert.Equal("New", d.Name);
        p.Touch(d, null);
        Assert.Equal("New", d.Name);
        Assert.Equal("New", Create().Devices.Single().Name);
    }

    [Fact]
    public void ChangedEventFiresOutsideTheLock()
    {
        var p = Create();
        using var fired = new ManualResetEventSlim();
        p.Changed += () =>
        {
            // Re-entering the store from the handler must not deadlock.
            _ = p.Devices;
            fired.Set();
        };
        p.NewPairingCode();
        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)));
    }
}
