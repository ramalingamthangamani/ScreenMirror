using ScreenMirror;

public class OptionsTests
{
    [Fact]
    public void Defaults()
    {
        var o = Options.Parse(Array.Empty<string>());
        Assert.Equal(Options.DefaultPort, o.Port);
        Assert.False(o.PortExplicit);
        Assert.False(o.NoTray);
        Assert.False(o.NoBrowser);
        Assert.False(o.Verbose);
        Assert.Null(o.WebRoot);
        Assert.EndsWith("ScreenMirror", o.DataDir);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("-p")]
    [InlineData("--PORT")]
    public void PortIsExplicit(string flag)
    {
        var o = Options.Parse(new[] { flag, "9000" });
        Assert.Equal(9000, o.Port);
        Assert.True(o.PortExplicit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    public void InvalidPortThrows(string value) =>
        Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "--port", value }));

    [Fact]
    public void MissingValueThrows() =>
        Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "--port" }));

    [Fact]
    public void FlagsAndPaths()
    {
        var o = Options.Parse(new[] { "--no-tray", "--no-browser", "-v", "--data-dir", "rel\\data", "--webroot", "web" });
        Assert.True(o.NoTray);
        Assert.True(o.NoBrowser);
        Assert.True(o.Verbose);
        Assert.True(Path.IsPathRooted(o.DataDir));
        Assert.True(Path.IsPathRooted(o.WebRoot));
        Assert.EndsWith("data", o.DataDir);
    }

    [Fact]
    public void HeadlessIsAliasForNoTray() => Assert.True(Options.Parse(new[] { "--headless" }).NoTray);

    [Fact]
    public void UnknownArgumentMentionsUsage()
    {
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(new[] { "--bogus" }));
        Assert.Contains("--bogus", ex.Message);
        Assert.Contains("--port", ex.Message);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void HelpThrowsUsage(string flag)
    {
        var ex = Assert.Throws<ArgumentException>(() => Options.Parse(new[] { flag }));
        Assert.Equal(Options.Usage, ex.Message);
    }
}
