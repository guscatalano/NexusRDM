using System.Linq;
using NexusRDM.Services;
using Xunit;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Pure tests for <see cref="NetworkDiscoveryService.ParseCidr"/>.
/// The /24-only contract is the most user-visible part of the
/// discovery feature, so it gets exercised across boundaries.
/// </summary>
public sealed class NetworkDiscoveryServiceTests
{
    [Fact]
    public void ParseCidr_Slash24_Yields256Addresses()
    {
        var addrs = NetworkDiscoveryService.ParseCidr("192.168.6.0/24").ToList();
        Assert.Equal(256, addrs.Count);
        Assert.Equal("192.168.6.0",   addrs.First().ToString());
        Assert.Equal("192.168.6.255", addrs.Last().ToString());
    }

    [Theory]
    [InlineData("192.168.1.0/16")]
    [InlineData("10.0.0.0/8")]
    [InlineData("192.168.1.0/25")]
    [InlineData("192.168.1.0/32")]
    [InlineData("192.168.1.0/0")]
    public void ParseCidr_NonSlash24_Throws(string cidr)
    {
        var ex = Assert.Throws<System.FormatException>(() => NetworkDiscoveryService.ParseCidr(cidr).ToList());
        Assert.Contains("/24", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("192.168.1.0")]            // missing prefix
    [InlineData("192.168.1.0/24/extra")]
    [InlineData("999.999.999.999/24")]     // invalid IPv4
    [InlineData("::1/24")]                  // IPv6 — IPv4 only
    public void ParseCidr_InvalidInput_Throws(string cidr)
    {
        Assert.Throws<System.FormatException>(() => NetworkDiscoveryService.ParseCidr(cidr).ToList());
    }

    [Fact]
    public void ParseCidr_NormalizesArbitraryHostBits_ToNetworkRange()
    {
        // 192.168.1.42/24 should produce the same range as 192.168.1.0/24
        // because the prefix masks off the host bits.
        var a = NetworkDiscoveryService.ParseCidr("192.168.1.42/24").Select(x => x.ToString()).ToList();
        var b = NetworkDiscoveryService.ParseCidr("192.168.1.0/24").Select(x => x.ToString()).ToList();
        Assert.Equal(b, a);
    }
}
