using ScreenMirror;

public class StaticAssetsTests
{
    [Theory]
    [InlineData("index.html")]
    [InlineData("client.js")]
    [InlineData("client.css")]
    [InlineData("connect.html")]
    [InlineData("connect.js")]
    [InlineData("icon.svg")]
    [InlineData("manifest.webmanifest")]
    public void EmbeddedFilesExist(string name)
    {
        Assert.True(StaticAssets.TryGet(name, null, out var data, out var etag));
        Assert.NotEmpty(data);
        Assert.Matches("^\"[0-9A-F]{16}\"$", etag);
    }

    [Theory]
    [InlineData("missing.html")]
    [InlineData("../ScreenMirror.dll")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("")]
    [InlineData("a/b.js")]
    [InlineData("C:\\Windows\\win.ini")]
    public void UnsafeOrUnknownNamesAreRejected(string name) =>
        Assert.False(StaticAssets.TryGet(name, null, out _, out _));

    [Fact]
    public void WebRootOverridesEmbedded()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ScreenMirrorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "index.html"), "<p>dev</p>");
            Assert.True(StaticAssets.TryGet("index.html", dir, out var data, out _));
            Assert.Equal("<p>dev</p>", System.Text.Encoding.UTF8.GetString(data));
            Assert.False(StaticAssets.TryGet("client.js", dir, out _, out _)); // not in the folder, no fallback
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("a.html", "text/html; charset=utf-8")]
    [InlineData("a.JS", "text/javascript; charset=utf-8")]
    [InlineData("a.css", "text/css; charset=utf-8")]
    [InlineData("a.svg", "image/svg+xml")]
    [InlineData("a.webmanifest", "application/manifest+json")]
    [InlineData("a.bin", "application/octet-stream")]
    public void ContentTypes(string name, string expected) => Assert.Equal(expected, StaticAssets.ContentType(name));
}
