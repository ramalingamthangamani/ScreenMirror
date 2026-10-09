using System.Text.Json;
using ScreenMirror;

public class ProtocolTests
{
    [Theory]
    [InlineData("low", StreamQuality.Low, true)]
    [InlineData("HIGH", StreamQuality.High, true)]
    [InlineData("balanced", StreamQuality.Balanced, true)]
    [InlineData("ultra", StreamQuality.Balanced, false)]
    [InlineData(null, StreamQuality.Balanced, false)]
    public void QualityPresetParse(string? s, StreamQuality expected, bool ok)
    {
        Assert.Equal(ok, QualityPreset.TryParse(s, out var q));
        Assert.Equal(expected, q);
        Assert.Equal(expected.ToString().ToLowerInvariant(), QualityPreset.Name(expected));
    }

    [Fact]
    public void QualityPresetsAreOrdered()
    {
        var low = QualityPreset.For(StreamQuality.Low);
        var mid = QualityPreset.For(StreamQuality.Balanced);
        var high = QualityPreset.For(StreamQuality.High);
        Assert.True(low.MaxDim < mid.MaxDim && mid.MaxDim < high.MaxDim);
        Assert.True(low.Jpeg <= mid.Jpeg && mid.Jpeg <= high.Jpeg);
        Assert.All(new[] { low, mid, high }, p => Assert.True(p.Refine > p.Jpeg));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("  My Phone  ", "My Phone")]
    [InlineData("a\u0000b\nc", "abc")]
    public void SanitizeStripsControlCharsAndTrims(string? input, string expected) =>
        Assert.Equal(expected, ClientSession.Sanitize(input, 40));

    [Fact]
    public void SanitizeTruncates() =>
        Assert.Equal(new string('x', 5), ClientSession.Sanitize(new string('x', 50), 5));

    [Fact]
    public void JsonHelpersAreLenient()
    {
        using var doc = JsonDocument.Parse("""{"s":"txt","n":1.5,"i":7,"big":1e12,"b":true,"nb":"yes","x":null}""");
        var e = doc.RootElement;
        Assert.True(e.Has("s"));
        Assert.False(e.Has("missing"));
        Assert.Equal("txt", e.Str("s"));
        Assert.Null(e.Str("n"));
        Assert.Null(e.Str("missing"));
        Assert.Equal(1.5, e.Num("n"));
        Assert.Equal(9, e.Num("s", 9));
        Assert.Equal(7, e.Int("i"));
        Assert.Equal(1, e.Int("n"));
        Assert.Equal(int.MaxValue, e.Int("big"));
        Assert.Equal(-1, e.Int("missing", -1));
        Assert.True(e.Bool("b"));
        Assert.True(e.Bool("nb", true)); // not a bool: default
        Assert.False(e.Bool("x"));
    }

    [Fact]
    public void MonitorListExposesIndexAndSize()
    {
        var list = ClientSession.MonitorList(new List<MonitorInfo>
        {
            new(0, @"\\.\DISPLAY1", 0, 0, 1920, 1080, true),
            new(1, @"\\.\DISPLAY2", 1920, 0, 2560, 1440, false),
        });
        string json = JsonSerializer.Serialize(list);
        Assert.Contains("\"i\":0", json);
        Assert.Contains("\"w\":1920", json);
        Assert.Contains("\"h\":1080", json);
        Assert.Contains("\"primary\":true", json);
        Assert.Contains("Display 2 - 2560x1440", json);
    }

    [Fact]
    public void ServerClockIsMonotonicAndNearWallClock()
    {
        double a = ServerClock.NowMs;
        double b = ServerClock.NowMs;
        Assert.True(b >= a);
        Assert.True(Math.Abs(a - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) < 5000);
    }
}
