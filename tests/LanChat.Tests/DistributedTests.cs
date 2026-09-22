using System.Security.Cryptography;
using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanChat.Tests;

public sealed class DistributedTests
{
    private static IDistributedStore Distributed(TestNode n) => n.Host.Services.GetRequiredService<IDistributedStore>();
    private static async Task<EventDto[]> Events(TestNode n)
    {
        var store = Distributed(n); var result = new List<EventDto>();
        foreach (var origin in (await store.GetDigestAsync()).Cursors)
        {
            long after = 0;
            while (after < origin.LastOriginSequence)
            {
                var batch = await store.GetEventRangeAsync(new(origin.OriginNodeId, after)); result.AddRange(batch.Events); after = batch.Events[^1].OriginSequence;
            }
        }
        return result.ToArray();
    }
    [Fact]
    public async Task GappedCursorDuplicateAndOutOfOrderEditConverge()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(); await b.InitializeAsync();
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        var message = await a.Store.SaveOutgoingAsync((await b.Store.GetIdentityAsync()).UserId, "Nội dung ban đầu");
        await Distributed(a).EditMessageAsync(message.Id, "Đã sửa nội dung");
        var events = (await Events(a)).OrderBy(x => x.OriginSequence).ToArray();
        await Distributed(b).ImportEventsAsync(new([events[^1]]));
        var digest = await Distributed(b).GetDigestAsync();
        Assert.Equal(0, digest.Cursors.Single(x => x.OriginNodeId == events[0].OriginNodeId).LastOriginSequence);
        foreach (var e in events.Reverse()) await Distributed(b).ImportEventsAsync(new([e]));
        await Distributed(b).ImportEventsAsync(new(events));
        var actual = Assert.Single(await b.Store.GetMessagesAsync(message.ConversationId));
        Assert.Equal("Đã sửa nội dung", actual.Text); Assert.True(actual.IsEdited);
        Assert.Equal(events.Length, (await Distributed(b).GetDigestAsync()).Cursors.Single(x => x.OriginNodeId == events[0].OriginNodeId).LastOriginSequence);
    }
    [Fact]
    public async Task RecallWinsOverLateEditLocalDeleteStaysLocalAndFtsUpdates()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(); await b.InitializeAsync(); await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        var m = await a.Store.SaveOutgoingAsync((await b.Store.GetIdentityAsync()).UserId, "Bình Mỹ Ninh Bình");
        Assert.Single(await Distributed(a).SearchMessagesAsync("binh my"));
        await Distributed(a).EditMessageAsync(m.Id, "Nội dung thay thế");
        Assert.Empty(await Distributed(a).SearchMessagesAsync("binh my"));
        await Distributed(a).RecallMessageAsync(m.Id);
        foreach (var e in (await Events(a)).Reverse()) await Distributed(b).ImportEventsAsync(new([e]));
        Assert.True(Assert.Single(await b.Store.GetMessagesAsync(m.ConversationId)).IsRecalled);
        Assert.Empty(await Distributed(a).SearchMessagesAsync("thay the"));
        await Distributed(b).DeleteLocalAsync(m.Id);
        Assert.Empty(await b.Store.GetMessagesAsync(m.ConversationId)); Assert.Single(await a.Store.GetMessagesAsync(m.ConversationId));
    }
    [Fact]
    public async Task ThirdNodeObtainsWholeHistoryWithoutBecomingParticipant()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B"); await using var c = new TestNode(cluster, secret, "C");
        await a.InitializeAsync(); await b.InitializeAsync(); await c.InitializeAsync();
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        var m = await a.Store.SaveOutgoingAsync((await b.Store.GetIdentityAsync()).UserId, "Lịch sử toàn mạng");
        await Distributed(c).ImportEventsAsync(new(await Events(a)));
        Assert.Single(await c.Store.GetMessagesAsync(m.ConversationId));
        Assert.Empty(await c.Store.GetConversationsAsync()); Assert.Empty(await Distributed(c).SearchMessagesAsync("lich su"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Distributed(c).SendToConversationAsync(m.ConversationId, "Không được gửi"));
    }
    [Fact]
    public async Task EventStoreRejectsMutationAtSqliteLevel()
    {
        await using var node = new TestNode(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), "A"); await node.InitializeAsync();
        await using var db = await node.Host.Services.GetRequiredService<IDbContextFactory<ChatDbContext>>().CreateDbContextAsync();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE ReplicationEvents SET PayloadJson='invalid'"));
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM ReplicationEvents"));
    }
    [Fact]
    public async Task ThreeVotersElectFailOverAndMinorityCannotChangeGroup()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32); var bootstrap = Guid.NewGuid();
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B"); await using var c = new TestNode(cluster, secret, "C");
        var nodes = new[] { a, b, c };
        foreach (var n in nodes) { n.Options.BootstrapNodeId = bootstrap; n.Options.LeaderTimeout = TimeSpan.FromSeconds(2); n.Options.HeartbeatInterval = TimeSpan.FromMilliseconds(200); n.Options.ReconciliationInterval = TimeSpan.FromSeconds(2); }
        a.Options.InitialNodeId = bootstrap;
        foreach (var n in nodes) await n.InitializeAsync();
        foreach (var n in nodes) foreach (var peer in nodes.Where(x => x != n)) await n.Store.RememberPeerAsync(await peer.Store.GetNodeInfoAsync(), "127.0.0.1");
        foreach (var n in nodes) await n.Host.StartAsync();
        var services = nodes.Select(n => n.Host.Services.GetRequiredService<IConsensusService>()).ToArray();
        await TestNode.WaitUntilAsync(async () => (await Task.WhenAll(services.Select(s => s.GetStatusAsync()))).All(s => s.Voters.Length == 3 && s.JointVoters is null), 35);
        var states = await Task.WhenAll(services.Select(x => x.GetStatusAsync()));
        Assert.Single(states, x => x.HasQuorum); var leaderIndex = Array.FindIndex(states, x => x.HasQuorum);
        var owner = (await a.Store.GetIdentityAsync()).UserId;
        var users = new[] { owner, (await b.Store.GetIdentityAsync()).UserId, (await c.Store.GetIdentityAsync()).UserId };
        var groupId = Guid.NewGuid();
        var create = await services[0].ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupCreated, owner, groupId, "Nhóm kiểm thử", Members: users));
        Assert.True(create.Success, create.Error);
        await TestNode.WaitUntilAsync(async () => (await Task.WhenAll(nodes.Select(n => Distributed(n).GetGroupsAsync()))).All(x => x.Length == 1));
        var denied = await services[1].ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupRenamed, users[1], groupId, "Không được đổi"));
        Assert.False(denied.Success);
        var groupMessage = await Distributed(b).SendToConversationAsync(groupId, "Chat nhóm đã được đồng bộ");
        await TestNode.WaitUntilAsync(async () => (await a.Store.GetMessagesAsync(groupId)).Any(x => x.Id == groupMessage.Id));
        var failedLeader = nodes[leaderIndex]; var failedId = (await failedLeader.Store.GetIdentityAsync()).NodeId;
        foreach (var n in nodes.Where(x => x != failedLeader))
        {
            var f = n.Host.Services.GetRequiredService<FaultInjection>(); lock (f.BlockedNodes) f.BlockedNodes.Add(failedId);
            var id = (await n.Store.GetIdentityAsync()).NodeId; var isolated = failedLeader.Host.Services.GetRequiredService<FaultInjection>(); lock (isolated.BlockedNodes) isolated.BlockedNodes.Add(id);
        }
        await TestNode.WaitUntilAsync(async () => (await Task.WhenAll(services.Select(x => x.GetStatusAsync()))).Where((_, i) => i != leaderIndex).Any(x => x.HasQuorum), 20);
        var isolatedStatus = await services[leaderIndex].GetStatusAsync(); Assert.False(isolatedStatus.HasQuorum);
        var isolatedUser = (await failedLeader.Store.GetIdentityAsync()).UserId;
        var rejected = await services[leaderIndex].ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupCreated, isolatedUser, Guid.NewGuid(), "Phải từ chối", Members: [isolatedUser]));
        Assert.False(rejected.Success);
        var offlineChat = await Distributed(failedLeader).SendToConversationAsync(groupId, "Tin vẫn lưu khi thiếu quorum");
        foreach (var n in nodes) { var f = n.Host.Services.GetRequiredService<FaultInjection>(); lock (f.BlockedNodes) f.BlockedNodes.Clear(); }
        await TestNode.WaitUntilAsync(async () => (await Task.WhenAll(services.Select(x => x.GetStatusAsync()))).Select(x => x.LeaderNodeId).Distinct().Count() == 1, 20);
        await TestNode.WaitUntilAsync(async () => (await Task.WhenAll(nodes.Select(n => n.Store.GetMessagesAsync(groupId)))).All(messages => messages.Any(x => x.Id == offlineChat.Id)), 20);
    }
    [Fact]
    public async Task ReactionsReceiptsReplyAndForwardConvergeWithOutOfOrderDelivery()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(); await b.InitializeAsync(); await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        var original = await a.Store.SaveOutgoingAsync((await b.Store.GetIdentityAsync()).UserId, "Nội dung gốc");
        await Distributed(b).ImportEventsAsync(new(await Events(a)));
        await Distributed(b).ReactAsync(original.Id, "👍", true); await Distributed(b).ReactAsync(original.Id, "👍", false); await Distributed(b).ReactAsync(original.Id, "❤️", true);
        await b.Store.SetConversationAsync(original.ConversationId, markRead: true);
        var reply = await Distributed(b).SendToConversationAsync(original.ConversationId, "Trả lời", replyTo: original.Id);
        var forwarded = await Distributed(b).SendToConversationAsync(original.ConversationId, original.Text, forwardedFrom: original.Id);
        foreach (var e in (await Events(b)).Reverse()) await Distributed(a).ImportEventsAsync(new([e]));
        var details = await Distributed(a).GetMessageDetailsAsync(original.Id);
        Assert.Equal(new[] { "❤️" }, details.Reactions); Assert.Equal(1, details.ReadCount); Assert.Equal(MessageStatus.Delivered, details.Message.Status);
        Assert.Equal(original.Text, (await Distributed(a).GetMessageDetailsAsync(reply.Id)).ReplyPreview);
        Assert.Equal(original.Id, (await Distributed(a).GetMessageDetailsAsync(forwarded.Id)).Message.ForwardedFromMessageId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Distributed(b).EditMessageAsync(original.Id, "Không được sửa"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Distributed(b).RecallMessageAsync(original.Id));
    }
    [Fact]
    public async Task NewNodeRecoversMoreThanOnePageIncludingThirdPartyHistoryOverTls()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B"); await using var d = new TestNode(cluster, secret, "D");
        await a.InitializeAsync(); await b.InitializeAsync(); await d.InitializeAsync();
        var recipient = await b.Store.GetNodeInfoAsync(); await a.Store.RememberPeerAsync(recipient, "127.0.0.1");
        var conversation = await a.Store.OpenDirectAsync(recipient.User.Id);
        for (var i = 0; i < 215; i++) await Distributed(a).SendToConversationAsync(conversation, $"Lịch sử khi D offline {i}");
        var page = await Distributed(a).GetEventRangeAsync(new((await a.Store.GetIdentityAsync()).NodeId, 0)); Assert.Equal(100, page.Events.Length);
        await a.Host.StartAsync(); await d.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        await d.Host.Services.GetRequiredService<ReplicationBackgroundService>().SynchronizeAsync(CancellationToken.None);
        var actual = await d.Store.GetMessagesAsync(conversation, limit: 500); Assert.Equal(215, actual.Count);
        Assert.Equal((await a.Store.GetMessagesAsync(conversation, limit: 500)).Select(x => x.Id), actual.Select(x => x.Id));
        Assert.Empty(await d.Store.GetConversationsAsync());
        var window = await Distributed(a).GetMessageWindowAsync(conversation, actual[100].Id);
        Assert.Equal(101, window.Length); Assert.Equal(actual[100].Id, window[50].Id);
    }
    [Fact]
    public async Task PersistedVotePreventsTwoCandidatesWinningTheSameTermAfterRestart()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32); var first = Guid.NewGuid(); var second = Guid.NewGuid(); string directory;
        await using (var voter = new TestNode(cluster, secret, "Voter"))
        {
            voter.Options.BootstrapNodeId = first; await voter.InitializeAsync(); directory = voter.Options.DataDirectory;
            await using var db = await voter.Host.Services.GetRequiredService<IDbContextFactory<ChatDbContext>>().CreateDbContextAsync();
            var config = new ControlCommand(Guid.NewGuid(), ControlCommandType.FinalMembership, Guid.Empty, Guid.Empty, NewVoters: [first, second, (await voter.Store.GetIdentityAsync()).NodeId]);
            db.ControlLog.Add(new ControlLogEntry { Id = config.Id, Index = 1, Term = 1, CommandJson = System.Text.Json.JsonSerializer.Serialize(config) }); await db.SaveChangesAsync();
            var consensus = voter.Host.Services.GetRequiredService<ConsensusService>();
            var answers = await Task.WhenAll(consensus.VoteAsync(new(2, first, 1, 1), first, CancellationToken.None), consensus.VoteAsync(new(2, second, 1, 1), second, CancellationToken.None));
            Assert.Single(answers, x => x.Granted);
            first = answers[0].Granted ? first : second; second = answers[0].Granted ? second : config.NewVoters![0];
        }
        await using var restarted = new TestNode(cluster, secret, "Voter", directory); restarted.Options.BootstrapNodeId = first; await restarted.InitializeAsync();
        var service = restarted.Host.Services.GetRequiredService<ConsensusService>();
        Assert.True((await service.VoteAsync(new(2, first, 1, 1), first, CancellationToken.None)).Granted);
        Assert.False((await service.VoteAsync(new(2, second, 1, 1), second, CancellationToken.None)).Granted);
        Assert.False((await service.VoteAsync(new(3, second, 0, 0), second, CancellationToken.None)).Granted);
        Assert.True((await service.VoteAsync(new(3, second, 1, 1), second, CancellationToken.None)).Granted);
    }
    [Fact]
    public async Task CommittedGroupOperationsEnforceOwnershipAndMembership()
    {
        var cluster = Guid.NewGuid(); var secret = RandomNumberGenerator.GetBytes(32); var bootstrap = Guid.NewGuid();
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B"); await using var c = new TestNode(cluster, secret, "C");
        a.Options.BootstrapNodeId = bootstrap; a.Options.InitialNodeId = bootstrap; a.Options.HeartbeatInterval = TimeSpan.FromMilliseconds(150);
        await a.InitializeAsync(); await b.InitializeAsync(); await c.InitializeAsync();
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1"); await a.Store.RememberPeerAsync(await c.Store.GetNodeInfoAsync(), "127.0.0.1"); await a.Host.StartAsync();
        var service = a.Host.Services.GetRequiredService<IConsensusService>(); await TestNode.WaitUntilAsync(async () => (await service.GetStatusAsync()).HasQuorum);
        var owner = (await a.Store.GetIdentityAsync()).UserId; var userB = (await b.Store.GetIdentityAsync()).UserId; var userC = (await c.Store.GetIdentityAsync()).UserId; var group = Guid.NewGuid();
        async Task Apply(ControlCommandType type, string? name = null, Guid? target = null, string? avatar = null)
        {
            var result = await service.ProposeAsync(new(Guid.NewGuid(), type, owner, group, name, target, Members: [owner, userB], AvatarBase64: avatar)); Assert.True(result.Success, result.Error);
        }
        await Apply(ControlCommandType.GroupCreated, "Nhóm đầu tiên"); await Apply(ControlCommandType.GroupRenamed, "Tên đã đổi");
        await Apply(ControlCommandType.GroupAvatarChanged, avatar: "aGVsbG8="); await Apply(ControlCommandType.GroupMemberAdded, target: userC); await Apply(ControlCommandType.GroupMemberRemoved, target: userB);
        var projected = Assert.Single(await Distributed(a).GetGroupsAsync()); Assert.Equal("Tên đã đổi", projected.Name); Assert.Contains(userC, projected.Members); Assert.DoesNotContain(userB, projected.Members);
        await Apply(ControlCommandType.GroupOwnerChanged, target: userC);
        Assert.Equal(userC, Assert.Single(await Distributed(a).GetGroupsAsync()).OwnerUserId);
        Assert.False((await service.ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupDeleted, owner, group))).Success);
        var second = Guid.NewGuid(); Assert.True((await service.ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupCreated, owner, second, "Xóa thử", Members: [owner]))).Success);
        Assert.True((await service.ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupDeleted, owner, second))).Success);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Distributed(a).SendToConversationAsync(second, "Nhóm đã xóa"));
    }
}
