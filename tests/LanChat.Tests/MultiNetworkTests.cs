using LanChat.Application;
using LanChat.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanChat.Tests;

public sealed class MultiNetworkTests
{
    [Fact]
    public async Task ClockSkewStillDiscoversBothPeersAndDeliversMessages()
    {
        var cluster = Guid.NewGuid();
        var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "Clock A");
        await using var b = new TestNode(cluster, secret, "Clock B",
            utcSeconds: () => DateTimeOffset.UtcNow.AddMinutes(17).ToUnixTimeSeconds());
        a.Options.DiscoveryTargets.Add("127.0.0.1:" + b.Options.DiscoveryPort);
        b.Options.DiscoveryTargets.Add("127.0.0.1:" + a.Options.DiscoveryPort);
        await a.InitializeAsync(true);
        await b.InitializeAsync(true);

        await TestNode.WaitUntilAsync(async () => (await a.Store.GetUsersAsync()).Count == 1 &&
            (await b.Store.GetUsersAsync()).Count == 1);
        var userA = Assert.Single(await b.Store.GetUsersAsync());
        var userB = Assert.Single(await a.Store.GetUsersAsync());
        Assert.Equal("Clock A", userA.DisplayName);
        Assert.Equal("Clock B", userB.DisplayName);
        var message = await a.Store.SaveOutgoingAsync(userB.UserId, "Kiểm tra kết nối khi lệch giờ");
        await TestNode.WaitUntilAsync(async () => (await b.Store.GetMessagesAsync(message.ConversationId)).Any(m => m.Id == message.Id));
    }

    [Fact]
    public async Task NetworksShareDiscoveryPortAndKeepDataSeparateAfterOneStops()
    {
        var hub = new DiscoverySockets();
        var port = TestNode.FreeUdpPort();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstSecret = FrameworkIO.RandomBytes(32);
        var secondSecret = FrameworkIO.RandomBytes(32);
        await using var localA = new TestNode(firstId, firstSecret, "Local A", discovery: hub);
        await using var localB = new TestNode(secondId, secondSecret, "Local B", discovery: hub);
        await using var peerA = new TestNode(firstId, firstSecret, "Peer A");
        await using var peerB = new TestNode(secondId, secondSecret, "Peer B");
        localA.Options.DiscoveryPort = localB.Options.DiscoveryPort = port;
        peerA.Options.DiscoveryTargets.Add("127.0.0.1:" + port);
        peerB.Options.DiscoveryTargets.Add("127.0.0.1:" + port);
        await localA.InitializeAsync(true);
        await localB.InitializeAsync(true);
        await peerA.InitializeAsync(true);
        await peerB.InitializeAsync(true);

        await TestNode.WaitUntilAsync(async () => (await localA.Store.GetUsersAsync()).Count == 1 &&
            (await localB.Store.GetUsersAsync()).Count == 1 && (await peerB.Store.GetUsersAsync()).Count == 1);
        Assert.Equal("Peer A", Assert.Single(await localA.Store.GetUsersAsync()).DisplayName);
        var peer = Assert.Single(await localB.Store.GetUsersAsync());
        Assert.Equal("Peer B", peer.DisplayName);
        var thread = await localB.Store.OpenDirectAsync(peer.UserId);

        await localA.Host.StopAsync();
        var sent = await localB.Store.SaveOutgoingAsync(peer.UserId, "Mạng B hoạt động sau khi gỡ mạng A");
        await TestNode.WaitUntilAsync(async () => (await peerB.Store.GetMessagesAsync(thread)).Any(m => m.Id == sent.Id));
        Assert.Empty(await peerA.Store.GetConversationsAsync());
        var identity = await localB.Store.GetIdentityAsync();
        var previousSeen = Assert.Single(await peerB.Store.GetUsersAsync()).LastSeenTicks;
        localB.Options.DiscoveryTargets.Add("127.0.0.1:" + peerB.Options.DiscoveryPort);
        localB.Host.Services.GetRequiredService<PeerDiscoveryBackgroundService>().RequestDiscovery();
        await TestNode.WaitUntilAsync(async () => (await peerB.Store.GetUsersAsync()).Any(u => u.NodeId == identity.NodeId && u.LastSeenTicks > previousSeen));
    }
}
