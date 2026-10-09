using ScreenMirror;

public class QrCodeTests
{
    [Theory]
    [InlineData("http://192.168.1.10:8642/?code=123456", 3)]
    [InlineData("a", 1)]
    public void PicksSmallestVersionAndSize(string text, int expectedVersion)
    {
        var qr = QrCode.Encode(text);
        Assert.Equal(expectedVersion, qr.Version);
        Assert.Equal(expectedVersion * 4 + 17, qr.Size);
    }

    [Fact]
    public void FinderPatternsArePresent()
    {
        var qr = QrCode.Encode("http://localhost:8642/?code=000000");
        int s = qr.Size;
        foreach (var (ox, oy) in new[] { (0, 0), (s - 7, 0), (0, s - 7) })
        {
            // 7x7 finder: dark ring, light ring, dark 3x3 centre.
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 7; x++)
                {
                    bool ring = x == 0 || y == 0 || x == 6 || y == 6;
                    bool centre = x >= 2 && x <= 4 && y >= 2 && y <= 4;
                    Assert.Equal(ring || centre, qr[ox + x, oy + y]);
                }
        }
        // Timing pattern alternates along row/column 6.
        for (int i = 8; i < s - 8; i++)
        {
            Assert.Equal(i % 2 == 0, qr[i, 6]);
            Assert.Equal(i % 2 == 0, qr[6, i]);
        }
        // Dark module next to the bottom-left finder.
        Assert.True(qr[8, s - 8]);
    }

    [Fact]
    public void OutOfRangeIndexIsLight()
    {
        var qr = QrCode.Encode("x");
        Assert.False(qr[-1, 0]);
        Assert.False(qr[0, qr.Size]);
    }

    [Fact]
    public void DifferentTextsGiveDifferentCodes()
    {
        var a = QrCode.Encode("http://10.0.0.1:8642/?code=111111");
        var b = QrCode.Encode("http://10.0.0.1:8642/?code=222222");
        Assert.Equal(a.Size, b.Size);
        bool differ = false;
        for (int y = 0; y < a.Size && !differ; y++)
            for (int x = 0; x < a.Size; x++)
                if (a[x, y] != b[x, y]) { differ = true; break; }
        Assert.True(differ);
    }

    [Fact]
    public void SvgHasViewBoxCoveringBorderAndModules()
    {
        var qr = QrCode.Encode("hello");
        string svg = qr.ToSvg(border: 2);
        Assert.StartsWith("<svg", svg.TrimStart());
        Assert.Contains($"viewBox=\"0 0 {qr.Size + 4} {qr.Size + 4}\"", svg);
        Assert.Contains("#000", svg);
        Assert.Contains("#fff", svg);
    }

    [Fact]
    public void TooLongTextThrows() =>
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('x', 3000)));
}
