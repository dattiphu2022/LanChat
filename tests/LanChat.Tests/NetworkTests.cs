using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using Xunit;

namespace LanChat.Tests;

public sealed class NetworkTests
{
    [Theory]
    [InlineData("255.255.255.0", 24, "192.168.1.0", "192.168.1.255", "192.168.1.1", "192.168.1.254", 254)]
    [InlineData("255.255.255.128", 25, "192.168.1.0", "192.168.1.127", "192.168.1.1", "192.168.1.126", 126)]
    [InlineData("255.255.255.192", 26, "192.168.1.64", "192.168.1.127", "192.168.1.65", "192.168.1.126", 62)]
    [InlineData("255.255.255.224", 27, "192.168.1.64", "192.168.1.95", "192.168.1.65", "192.168.1.94", 30)]
    [InlineData("255.255.255.240", 28, "192.168.1.64", "192.168.1.79", "192.168.1.65", "192.168.1.78", 14)]
    public void CalculatesSubnetsAndExcludesOwnNetworkAndBroadcast(string mask, int prefix, string network, string broadcast, string first, string last, int hosts)
    {
        var range = NetworkRangeCalculator.Calculate("192.168.1.65", mask);
        Assert.Equal(prefix, range.PrefixLength); Assert.Equal(network, range.Network); Assert.Equal(broadcast, range.Broadcast);
        Assert.Equal(first, range.FirstHost); Assert.Equal(last, range.LastHost); Assert.Equal(hosts, range.HostCount);
        var candidates = NetworkRangeCalculator.Hosts(range).ToArray();
        Assert.Equal(hosts - 1, candidates.Length); Assert.DoesNotContain("192.168.1.65", candidates);
        Assert.DoesNotContain(network, candidates); Assert.DoesNotContain(broadcast, candidates);
    }
    [Fact]
    public void CalculatesNon24LargeSubnetLazily()
    {
        var range = NetworkRangeCalculator.Calculate("10.40.8.9", "255.255.0.0");
        Assert.Equal("10.40.0.0", range.Network); Assert.Equal("10.40.255.255", range.Broadcast);
        Assert.Equal(65534, range.HostCount); Assert.Equal("10.40.0.1", NetworkRangeCalculator.Hosts(range).First());
    }
    [Theory]
    [InlineData("255.255.255.254", 2)]
    [InlineData("255.255.255.255", 1)]
    public void HandlesPointToPointAndSingleHost(string mask, int count) => Assert.Equal(count, NetworkRangeCalculator.Calculate("10.0.0.2", mask).HostCount);
    [Fact]
    public void RejectsInvalidMask() => Assert.Throws<ArgumentException>(() => NetworkRangeCalculator.Calculate("10.0.0.1", "255.0.255.0"));
    [Fact]
    public void DirectConversationIsStableAndClusterScoped()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var cluster = Guid.NewGuid();
        Assert.Equal(DirectConversation.GetId(cluster, a, b), DirectConversation.GetId(cluster, b, a));
        Assert.NotEqual(DirectConversation.GetId(cluster, a, b), DirectConversation.GetId(Guid.NewGuid(), a, b));
    }
    [Fact]
    public void HybridClockAdvancesBeyondRemoteClock()
    {
        var future = DateTime.UtcNow.AddHours(1).Ticks;
        var clock = HybridClock.Advance(0, 0, future, 20);
        Assert.Equal(future, clock.Physical); Assert.Equal(21, clock.Logical);
        Assert.Equal(22, HybridClock.Advance(clock.Physical, clock.Logical).Logical);
    }
    [Fact]
    public void HmacRejectsWrongClusterSecretTamperingAndReplay()
    {
        var options = new NodeOptions { ClusterId = Guid.NewGuid(), ClusterSecret = FrameworkIO.RandomBytes(32) };
        var wire = new WireProtocol(options); var packet = wire.Encode(Guid.NewGuid(), "NodeInfo", new { Text = "hello" });
        Assert.Equal("NodeInfo", wire.Decode(packet).MessageType);
        Assert.Throws<InvalidDataException>(() => wire.Decode(packet));
        var other = new WireProtocol(new NodeOptions { ClusterId = options.ClusterId, ClusterSecret = FrameworkIO.RandomBytes(32) });
        Assert.Throws<InvalidDataException>(() => other.Decode(packet));
        var altered = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(packet).Replace("hello", "other"));
        Assert.Throws<InvalidDataException>(() => wire.Decode(altered, false));
    }
    [Fact]
    public async Task RejectsOversizedFrameBeforeAllocation()
    {
        using var stream = new MemoryStream(new byte[] { 127, 255, 255, 255 });
        await Assert.ThrowsAsync<InvalidDataException>(() => WireProtocol.ReadAsync(stream, CancellationToken.None));
    }
}
