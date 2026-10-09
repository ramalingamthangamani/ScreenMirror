using ScreenMirror;

public class NetworkTests
{
    [Theory]
    [InlineData("192.168.1.10:8642")]
    [InlineData("10.0.0.5")]
    [InlineData("[::1]:8642")]
    [InlineData("[fe80::1]")]
    [InlineData("localhost:8642")]
    [InlineData("LOCALHOST")]
    [InlineData("localhost.:8642")]
    public void AllowsIpLiteralsAndLocalhost(string host) => Assert.True(Network.IsAllowedHost(host));

    [Fact]
    public void AllowsMachineNameWithOrWithoutSuffix()
    {
        string name = Environment.MachineName;
        Assert.True(Network.IsAllowedHost(name + ":8642"));
        Assert.True(Network.IsAllowedHost(name.ToLowerInvariant()));
        Assert.True(Network.IsAllowedHost(name + ".local:8642"));
        Assert.True(Network.IsAllowedHost(name + ".lan"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("evil.example:8642")]
    [InlineData("attacker.com")]
    [InlineData("[not-an-ip]:8642")]
    [InlineData("notlocalhost")]
    public void RejectsForeignNames(string? host) => Assert.False(Network.IsAllowedHost(host));

    [Fact]
    public void RejectsNameThatMerelyStartsWithMachineName() =>
        Assert.False(Network.IsAllowedHost(Environment.MachineName + "evil.com"));

    [Theory]
    [InlineData(null, "192.168.1.10:8642")]
    [InlineData("", "192.168.1.10:8642")]
    [InlineData("http://192.168.1.10:8642", "192.168.1.10:8642")]
    [InlineData("HTTP://192.168.1.10:8642", "192.168.1.10:8642")]
    [InlineData("http://localhost:8642", "localhost:8642")]
    [InlineData("http://localhost", "localhost:80")]
    public void SameOriginAccepted(string? origin, string host) => Assert.True(Network.IsSameOrigin(origin, host));

    [Theory]
    [InlineData("http://attacker.example", "192.168.1.10:8642")]
    [InlineData("http://192.168.1.10:9999", "192.168.1.10:8642")]
    [InlineData("null", "192.168.1.10:8642")]
    [InlineData("garbage", "192.168.1.10:8642")]
    public void ForeignOriginRejected(string origin, string host) => Assert.False(Network.IsSameOrigin(origin, host));

    [Fact]
    public void UrlsAlwaysReturnsAtLeastOne()
    {
        var urls = Network.Urls(1234);
        Assert.NotEmpty(urls);
        Assert.All(urls, u => Assert.Matches(@"^http://[^/]+:1234/$", u));
    }

    [Fact]
    public void LanAddressesAreIPv4NonLoopback()
    {
        foreach (var ip in Network.LanAddresses())
        {
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, ip.AddressFamily);
            Assert.False(System.Net.IPAddress.IsLoopback(ip));
        }
    }
}
