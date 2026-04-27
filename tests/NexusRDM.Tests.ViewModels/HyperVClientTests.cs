using NexusRDM.Services;
using Xunit;

namespace NexusRDM.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="HyperVClient.TryExtractIp"/>. The KVP exchange
/// items are XML strings the client parses to find a guest IP — same
/// shape as <c>cimv2</c> reports them. WMI access itself isn't
/// testable headless, but the parser is pure and worth pinning.
/// </summary>
public sealed class HyperVClientTests
{
    [Fact]
    public void TryExtractIp_NetworkAddressIPv4_ReturnsFirstNonLoopback()
    {
        const string xml = """
            <INSTANCE CLASSNAME="Msvm_KvpExchangeDataItem">
              <PROPERTY NAME="Name" TYPE="string"><VALUE>NetworkAddressIPv4</VALUE></PROPERTY>
              <PROPERTY NAME="Data" TYPE="string"><VALUE>127.0.0.1;192.168.1.50</VALUE></PROPERTY>
              <PROPERTY NAME="Source" TYPE="uint32"><VALUE>2</VALUE></PROPERTY>
            </INSTANCE>
            """;
        Assert.Equal("192.168.1.50", HyperVClient.TryExtractIp(xml));
    }

    [Fact]
    public void TryExtractIp_AllLoopback_ReturnsNull()
    {
        const string xml = """
            <INSTANCE CLASSNAME="Msvm_KvpExchangeDataItem">
              <PROPERTY NAME="Name"><VALUE>NetworkAddressIPv4</VALUE></PROPERTY>
              <PROPERTY NAME="Data"><VALUE>127.0.0.1</VALUE></PROPERTY>
            </INSTANCE>
            """;
        Assert.Null(HyperVClient.TryExtractIp(xml));
    }

    [Fact]
    public void TryExtractIp_NotANetworkAddressKey_ReturnsNull()
    {
        // The KVP feed includes lots of items (FullyQualifiedDomainName,
        // OSName, etc.). We only care about NetworkAddressIPv4.
        const string xml = """
            <INSTANCE CLASSNAME="Msvm_KvpExchangeDataItem">
              <PROPERTY NAME="Name"><VALUE>OSName</VALUE></PROPERTY>
              <PROPERTY NAME="Data"><VALUE>Ubuntu 24.04</VALUE></PROPERTY>
            </INSTANCE>
            """;
        Assert.Null(HyperVClient.TryExtractIp(xml));
    }

    [Fact]
    public void TryExtractIp_MalformedXml_ReturnsNull()
    {
        Assert.Null(HyperVClient.TryExtractIp("<not really xml"));
    }

    [Fact]
    public void TryExtractIp_LinkLocalSkipped()
    {
        const string xml = """
            <INSTANCE CLASSNAME="Msvm_KvpExchangeDataItem">
              <PROPERTY NAME="Name"><VALUE>NetworkAddressIPv4</VALUE></PROPERTY>
              <PROPERTY NAME="Data"><VALUE>169.254.0.1;10.0.0.7</VALUE></PROPERTY>
            </INSTANCE>
            """;
        Assert.Equal("10.0.0.7", HyperVClient.TryExtractIp(xml));
    }
}
