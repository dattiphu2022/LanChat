using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanChat.Tests;

public sealed class MultiNetworkTests
{
    [Fact]
    public async Task ReplicationTriesOtherAddressesAndKeepsTheWorkingRoute()
    {
        var cluster = Guid.NewGuid();
        var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A");
        await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync();
        await b.InitializeAsync(true);

        var store = a.Host.Services.GetRequiredService<ChatStore>();
        var peer = await b.Store.GetNodeInfoAsync();
        var oldPort = TestNode.FreeTcpPort();
        b.Options.ServicePort = oldPort;
        var staleAddress = await b.Store.GetNodeInfoAsync();
        b.Options.ServicePort = peer.ServicePort;
        await store.ObservePeerAsync(staleAddress, "127.0.0.2", ChatStore.PeerObservation.Discovery, default);
        await store.ObservePeerAsync(peer, "127.0.0.1", ChatStore.PeerObservation.Discovery, default);

        var before = Assert.Single(await store.GetUsersAsync());
        Assert.Equal("127.0.0.2", before.IPAddress);
        Assert.Equal(oldPort, before.ServicePort);
        Assert.Equal(PresenceStatus.Offline, before.Presence);
        var message = await a.Store.SaveOutgoingAsync(peer.User.Id, "Gửi qua địa chỉ dự phòng");
        await a.Host.Services.GetRequiredService<ReplicationBackgroundService>().SynchronizeAsync(default);

        Assert.Equal(message.Id, Assert.Single(await b.Store.GetMessagesAsync(message.ConversationId)).Id);
        var connected = Assert.Single(await store.GetUsersAsync());
        Assert.Equal(PresenceStatus.Online, connected.Presence);
        Assert.Equal("127.0.0.1", connected.IPAddress);
        Assert.Equal(peer.ServicePort, connected.ServicePort);
        var revision = store.PresenceRevision;
        await store.ObservePeerAsync(staleAddress, "127.0.0.2", ChatStore.PeerObservation.Discovery, default);
        await store.ObservePeerAsync(peer, "127.0.0.1", ChatStore.PeerObservation.Discovery, default);
        Assert.Equal(revision, store.PresenceRevision);
        Assert.Equal(connected, Assert.Single(await store.GetUsersAsync()));
        Assert.Single(await store.GetPeerEndpointsAsync());
    }

    [Fact]
    public async Task AWorkingTcpDirectionCanCarryRepliesWithoutUdp()
    {
        var cluster = Guid.NewGuid();
        var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A");
        await using var b = new TestNode(cluster, secret, "B");
        a.Options.ReconciliationInterval = TimeSpan.FromHours(1);
        await a.InitializeAsync();
        await b.InitializeAsync(true);
        var nodeA = await a.Store.GetNodeInfoAsync();
        var nodeB = await b.Store.GetNodeInfoAsync();
        await a.Store.RememberPeerAsync(nodeB, "127.0.0.1");

        // Run only A's replication service. A has no TCP listener or UDP discovery socket.
        var replication = a.Host.Services.GetRequiredService<ReplicationBackgroundService>();
        var sent = await a.Store.SaveOutgoingAsync(nodeB.User.Id, "Tin từ A");
        await replication.SynchronizeAsync(default);
        Assert.Equal(sent.Id, Assert.Single(await b.Store.GetMessagesAsync(sent.ConversationId)).Id);
        Assert.Equal(nodeA.NodeId, Assert.Single(await b.Store.GetUsersAsync()).NodeId);

        var faults = a.Host.Services.GetRequiredService<FaultInjection>();
        var initialSync = faults.LastSync;
        await replication.StartAsync(default);
        try
        {
            await TestNode.WaitUntilAsync(async () => faults.LastSync > initialSync && await a.Store.GetPendingCountAsync() == 0);
            var lastSeen = Assert.Single(await a.Store.GetUsersAsync()).LastSeenTicks;
            var reply = await b.Store.SaveOutgoingAsync(nodeA.User.Id, "Tin trả lời từ B");
            await TestNode.WaitUntilAsync(async () => (await a.Store.GetMessagesAsync(sent.ConversationId)).Any(m => m.Id == reply.Id), seconds: 12);
            var peer = Assert.Single(await a.Store.GetUsersAsync());
            Assert.True(peer.LastSeenTicks > lastSeen);
            Assert.Equal(PresenceStatus.Online, peer.Presence);
            Assert.Equal(PresenceStatus.Online, Assert.Single(await b.Store.GetUsersAsync()).Presence);
        }
        finally { await replication.StopAsync(default); }
    }

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
        var firstPeer = Assert.Single(await localA.Store.GetUsersAsync());
        Assert.Equal("Peer A", firstPeer.DisplayName);
        var peer = Assert.Single(await localB.Store.GetUsersAsync());
        Assert.Equal("Peer B", peer.DisplayName);
        var thread = await localB.Store.OpenDirectAsync(peer.UserId);
        var firstMessage = await localA.Store.SaveOutgoingAsync(firstPeer.UserId, "Tin từ mạng A");
        var secondMessage = await localB.Store.SaveOutgoingAsync(peer.UserId, "Tin từ mạng B khi cả hai mạng đang mở");
        await TestNode.WaitUntilAsync(async () => (await peerA.Store.GetMessagesAsync(firstMessage.ConversationId)).Any(m => m.Id == firstMessage.Id) &&
            (await peerB.Store.GetMessagesAsync(thread)).Any(m => m.Id == secondMessage.Id));

        await localA.Host.StopAsync();
        var sent = await localB.Store.SaveOutgoingAsync(peer.UserId, "Mạng B hoạt động sau khi gỡ mạng A");
        await TestNode.WaitUntilAsync(async () => (await peerB.Store.GetMessagesAsync(thread)).Any(m => m.Id == sent.Id));
        var localUser = (await localB.Store.GetIdentityAsync()).UserId;
        var reply = await peerB.Store.SaveOutgoingAsync(localUser, "Trả lời từ mạng B");
        await TestNode.WaitUntilAsync(async () => (await localB.Store.GetMessagesAsync(thread)).Any(m => m.Id == reply.Id));
        Assert.Empty(await peerA.Store.GetMessagesAsync(thread));
        var identity = await localB.Store.GetIdentityAsync();
        var previousSeen = Assert.Single(await peerB.Store.GetUsersAsync()).LastSeenTicks;
        localB.Options.DiscoveryTargets.Add("127.0.0.1:" + peerB.Options.DiscoveryPort);
        localB.Host.Services.GetRequiredService<PeerDiscoveryBackgroundService>().RequestDiscovery();
        await TestNode.WaitUntilAsync(async () => (await peerB.Store.GetUsersAsync()).Any(u => u.NodeId == identity.NodeId && u.LastSeenTicks > previousSeen));
    }
}
