using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LanChat.Tests;

public sealed class ChatIntegrationTests
{
    [Fact]
    public async Task MigrationCreatesIndexedWalDatabaseAndIdentitySurvivesRestartAndRename()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32); string directory; NodeIdentity initial;
        await using (var node = new TestNode(cluster, secret, "Nguyễn Văn A"))
        {
            await node.InitializeAsync(); directory = node.Options.DataDirectory; initial = await node.Store.GetIdentityAsync();
            var profile = await node.Store.GetProfileAsync(); profile.DisplayName = "Tên mới"; await node.Store.SaveProfileAsync(profile);
            await using var db = await node.Host.Services.GetRequiredService<IDbContextFactory<ChatDbContext>>().CreateDbContextAsync();
            Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count()); Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
            var connection = db.Database.GetDbConnection(); await connection.OpenAsync();
            using var command = connection.CreateCommand(); command.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", await command.ExecuteScalarAsync());
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='IX_Messages_OriginNodeId_OriginSequence'";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        await using var restarted = new TestNode(cluster, secret, "Ignored", directory); await restarted.InitializeAsync();
        var identity = await restarted.Store.GetIdentityAsync(); Assert.Equal(initial.NodeId, identity.NodeId); Assert.Equal(initial.UserId, identity.UserId);
        Assert.Equal("Tên mới", (await restarted.Store.GetProfileAsync()).DisplayName);
    }

    [Fact]
    public async Task DuplicateDeliveryIsIdempotentAndConflictingIdIsRejected()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(); await b.InitializeAsync();
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        var message = await a.Store.SaveOutgoingAsync((await b.Store.GetIdentityAsync()).UserId, "Xin chào 👋\nDòng hai");
        var push = new ChatPushDto(await a.Store.GetNodeInfoAsync(), PendingMessageDeliveryService.ToDto(message));
        await b.Store.ReceiveAsync(push, "127.0.0.1"); await b.Store.ReceiveAsync(push, "127.0.0.1");
        Assert.Single(await b.Store.GetMessagesAsync(message.ConversationId));
        await Assert.ThrowsAsync<InvalidDataException>(() => b.Store.ReceiveAsync(push with { Message = push.Message with { Text = "Modified" } }, "127.0.0.1"));
        Assert.Equal("Xin chào 👋\nDòng hai", (await b.Store.GetMessagesAsync(message.ConversationId))[0].Text);
    }

    [Fact]
    public async Task UdpDiscoveryAndBidirectionalTcpChatWorkWithoutServer()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "Máy A"); await using var b = new TestNode(cluster, secret, "Máy B");
        // Two real bound UDP sockets; directed loopback target makes the test independent of firewall broadcast policy.
        a.Options.DiscoveryTargets.Add($"127.0.0.1:{b.Options.DiscoveryPort}"); b.Options.DiscoveryTargets.Add($"127.0.0.1:{a.Options.DiscoveryPort}");
        await a.InitializeAsync(true); await b.InitializeAsync(true);
        await TestNode.WaitUntilAsync(async () => (await a.Store.GetUsersAsync()).Count == 1 && (await b.Store.GetUsersAsync()).Count == 1);
        var userA = (await a.Store.GetIdentityAsync()).UserId; var userB = (await b.Store.GetIdentityAsync()).UserId;
        var sent = await a.Host.Services.GetRequiredService<IChatService>().SendAsync(userB, "Xin chào từ A 👋");
        Assert.Equal(MessageStatus.LocalPending, sent.Status);
        await TestNode.WaitUntilAsync(async () => (await b.Store.GetMessagesAsync(sent.ConversationId)).Count == 1 && await a.Store.GetPendingCountAsync() == 0);
        var reply = await b.Host.Services.GetRequiredService<IChatService>().SendAsync(userA, "B đã nhận\nĐây là trả lời");
        Assert.Equal(sent.ConversationId, reply.ConversationId);
        await TestNode.WaitUntilAsync(async () => (await a.Store.GetMessagesAsync(sent.ConversationId)).Count == 2 && await b.Store.GetPendingCountAsync() == 0);
        Assert.Equal((await a.Store.GetMessagesAsync(sent.ConversationId)).Select(x => x.Id), (await b.Store.GetMessagesAsync(sent.ConversationId)).Select(x => x.Id));
        Assert.Null(a.Host.Services.GetRequiredService<NetworkHealth>().Error); Assert.Null(b.Host.Services.GetRequiredService<NetworkHealth>().Error);
    }

    [Fact]
    public async Task NetworkFailureRetainsPendingMessageAndRestartRetriesOverRealTcp()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var b = new TestNode(cluster, secret, "B"); await b.InitializeAsync();
        string directory; Message saved;
        await using (var a = new TestNode(cluster, secret, "A"))
        {
            await a.InitializeAsync(); directory = a.Options.DataDirectory;
            await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
            saved = await a.Store.SaveOutgoingAsync((await b.Store.GetIdentityAsync()).UserId, "Giữ lại dù máy B chưa mở");
            var delivery = new PendingMessageDeliveryService(a.Store, a.Host.Services.GetRequiredService<IPeerTransport>(), NullLogger<PendingMessageDeliveryService>.Instance);
            await delivery.DeliverPendingAsync(CancellationToken.None);
            Assert.Equal(1, await a.Store.GetPendingCountAsync());
            var local = Assert.Single(await a.Store.GetMessagesAsync(saved.ConversationId)); Assert.Equal(1, local.RetryCount); Assert.Equal(MessageStatus.LocalPending, local.Status);
        }
        await b.Host.StartAsync();
        await using var restarted = new TestNode(cluster, secret, "A", directory); await restarted.InitializeAsync(true);
        await TestNode.WaitUntilAsync(async () => await restarted.Store.GetPendingCountAsync() == 0 && (await b.Store.GetMessagesAsync(saved.ConversationId)).Count == 1);
        Assert.Equal(saved.Id, (await b.Store.GetMessagesAsync(saved.ConversationId))[0].Id);
    }

    [Fact]
    public async Task WrongClusterCannotDiscoverOrSendToTcpNode()
    {
        await using var a = new TestNode(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "A");
        await using var b = new TestNode(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "B");
        await a.InitializeAsync(); await b.InitializeAsync(true);
        await Assert.ThrowsAnyAsync<IOException>(() => a.Host.Services.GetRequiredService<IPeerTransport>().GetNodeInfoAsync("127.0.0.1", b.Options.ServicePort, CancellationToken.None));
        Assert.Empty(await b.Store.GetUsersAsync());
    }

    [Fact]
    public async Task OfflineContactIsRetainedAndAddressChangesDoNotChangeConversation()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(); await b.InitializeAsync(); var peer = await b.Store.GetNodeInfoAsync();
        await a.Store.RememberPeerAsync(peer, "10.0.0.2"); var first = await a.Store.OpenDirectAsync(peer.User.Id);
        await using (var db = await a.Host.Services.GetRequiredService<IDbContextFactory<ChatDbContext>>().CreateDbContextAsync())
            await db.Peers.ExecuteUpdateAsync(x => x.SetProperty(p => p.LastSeenTicks, DateTime.UtcNow.AddMinutes(-1).Ticks));
        Assert.Equal(PresenceStatus.Offline, Assert.Single(await a.Store.GetUsersAsync()).Presence);
        await a.Store.RememberPeerAsync(peer with { User = peer.User with { DisplayName = "Tên khác" } }, "10.0.0.99");
        Assert.Equal(first, await a.Store.OpenDirectAsync(peer.User.Id));
        Assert.Equal("10.0.0.99", Assert.Single(await a.Store.GetUsersAsync()).IPAddress);
    }

    [Fact]
    public async Task ConcurrentLocalSendsAllocateUniquePersistentSequences()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(); await b.InitializeAsync(); var peer = await b.Store.GetNodeInfoAsync();
        await a.Store.RememberPeerAsync(peer, "127.0.0.1");
        var sends = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => a.Store.SaveOutgoingAsync(peer.User.Id, $"Tin {i}")));
        Assert.Equal(20, sends.Select(x => x.OriginSequence).Distinct().Count()); Assert.Equal(20, await a.Store.GetPendingCountAsync());
    }

    [Fact]
    public async Task ScannerUsesTcpAndExcludesLocalAddressWithoutPing()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(); await b.InitializeAsync(true);
        a.Options.ServicePort = b.Options.ServicePort;
        var scanner = new LanScanner(new FixedNetworks(), a.Host.Services.GetRequiredService<IPeerTransport>(), a.Store, a.Options, NullLogger<LanScanner>.Instance);
        var peers = await scanner.ScanAsync(); Assert.Single(peers); Assert.Equal((await b.Store.GetIdentityAsync()).NodeId, peers[0].NodeId);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(canceled.Token));
    }
    private sealed class FixedNetworks : ILocalNetworkInfoProvider
    {
        public IReadOnlyList<NetworkInfo> GetNetworks() => [NetworkRangeCalculator.Calculate("127.0.0.2", "255.255.255.252")];
    }

    [Fact]
    public async Task LateMessageWithOldSenderClockStillCountsAsUnread()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(); await b.InitializeAsync();
        var bInfo = await b.Store.GetNodeInfoAsync(); await a.Store.RememberPeerAsync(bInfo, "127.0.0.1");
        var first = await a.Store.SaveOutgoingAsync(bInfo.User.Id, "Tin đầu");
        var sender = await a.Store.GetNodeInfoAsync();
        await b.Store.ReceiveAsync(new(sender, PendingMessageDeliveryService.ToDto(first)), "127.0.0.1");
        await b.Store.SetConversationAsync(first.ConversationId, markRead: true);
        Assert.Equal(0, Assert.Single(await b.Store.GetConversationsAsync()).UnreadCount);
        var second = await a.Store.SaveOutgoingAsync(bInfo.User.Id, "Tin đến muộn");
        var older = PendingMessageDeliveryService.ToDto(second) with { CreatedAtTicks = DateTime.UtcNow.AddDays(-7).Ticks };
        await b.Store.ReceiveAsync(new(sender, older), "127.0.0.1");
        Assert.Equal(1, Assert.Single(await b.Store.GetConversationsAsync()).UnreadCount);
    }

    [Fact]
    public async Task UpgradeFromInitialSchemaRetainsUserIdentity()
    {
        await using var node = new TestNode(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "Upgrade");
        await using var db = await node.Host.Services.GetRequiredService<IDbContextFactory<ChatDbContext>>().CreateDbContextAsync();
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20260921071741_InitialPhase1");
        var user = Guid.NewGuid(); var nodeId = Guid.NewGuid();
        db.Users.Add(new LocalUserProfile { Id = user, DisplayName = "Trước nâng cấp" });
        db.Settings.Add(new LocalSettings { Id = Guid.NewGuid(), UserId = user, NodeId = nodeId, CreatedAtTicks = DateTime.UtcNow.Ticks });
        await db.SaveChangesAsync();
        await node.InitializeAsync();
        Assert.Equal(user, (await node.Store.GetIdentityAsync()).UserId); Assert.Equal(nodeId, (await node.Store.GetIdentityAsync()).NodeId);
        Assert.Equal("Trước nâng cấp", (await node.Store.GetProfileAsync()).DisplayName);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
