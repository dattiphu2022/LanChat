using System.Security.Cryptography;
using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
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
                var batch = await store.GetEventRangeAsync(new(origin.OriginNodeId, after)); result.AddRange(batch.Events); after = batch.Events[batch.Events.Length - 1].OriginSequence;
            }
        }
        return result.ToArray();
    }
    [Fact]
    public async Task GappedCursorDuplicateAndOutOfOrderEditConverge()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(); await b.InitializeAsync();
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        var message = await a.Store.SaveOutgoingAsync((await b.Store.GetIdentityAsync()).UserId, "Nội dung ban đầu");
        await Distributed(a).EditMessageAsync(message.Id, "Đã sửa nội dung");
        var events = (await Events(a)).OrderBy(x => x.OriginSequence).ToArray();
        await Distributed(b).ImportEventsAsync(new([events[events.Length - 1]]));
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
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
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
    public async Task ThirdNodeRelaysCiphertextWithoutProjectingOrSearchingPrivateMessages()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B"); await using var c = new TestNode(cluster, secret, "C");
        await a.InitializeAsync(); await b.InitializeAsync(); await c.InitializeAsync();
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        var m = await a.Store.SaveOutgoingAsync((await b.Store.GetIdentityAsync()).UserId, "PRIVATE MESSAGE 67cd1290");
        await Distributed(c).ImportEventsAsync(new(await Events(a)));
        Assert.Empty(await c.Store.GetMessagesAsync(m.ConversationId));
        Assert.Empty(await c.Store.GetConversationsAsync()); Assert.Empty(await Distributed(c).SearchMessagesAsync("PRIVATE MESSAGE"));
        var relayed = (await Events(c)).Where(x => x.OriginNodeId == m.OriginNodeId).ToArray();
        var created = Assert.Single(relayed, x => x.EntityId == m.Id);
        Assert.DoesNotContain(m.Text, created.PayloadJson);
        await Assert.ThrowsAsync<InvalidDataException>(() => Distributed(b).ImportEventsAsync(new([created with { PayloadJson = "{}" }])));
        await Distributed(b).ImportEventsAsync(new(relayed));
        Assert.Equal(m.Text, Assert.Single(await b.Store.GetMessagesAsync(m.ConversationId)).Text);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Distributed(c).SendToConversationAsync(m.ConversationId, "Không được gửi"));
    }
    [Fact]
    public async Task EventStoreRejectsMutationAtSqliteLevel()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        using var db = node.Host.Services.GetRequiredService<ChatDatabase>().Open();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => db.Execute("UPDATE ReplicationEvents SET PayloadJson='invalid'"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => db.Execute("DELETE FROM ReplicationEvents"));
    }
    [Fact]
    public async Task ThreeVotersElectFailOverAndMinorityCannotChangeGroup()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32); var bootstrap = Guid.NewGuid();
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
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
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
    public async Task RelaySynchronizesMoreThanOnePageOfOpaqueHistoryOverTls()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B"); await using var d = new TestNode(cluster, secret, "D");
        await a.InitializeAsync(); await b.InitializeAsync(); await d.InitializeAsync();
        var recipient = await b.Store.GetNodeInfoAsync(); await a.Store.RememberPeerAsync(recipient, "127.0.0.1");
        var conversation = await a.Store.OpenDirectAsync(recipient.User.Id);
        for (var i = 0; i < 215; i++) await Distributed(a).SendToConversationAsync(conversation, $"Tin nhắn riêng số {i}");
        var page = await Distributed(a).GetEventRangeAsync(new((await a.Store.GetIdentityAsync()).NodeId, 0)); Assert.Equal(100, page.Events.Length);
        await a.Host.StartAsync(); await d.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        await d.Host.Services.GetRequiredService<ReplicationBackgroundService>().SynchronizeAsync(CancellationToken.None);
        Assert.Empty(await d.Store.GetMessagesAsync(conversation, limit: 500));
        var origin = (await a.Store.GetIdentityAsync()).NodeId;
        var relayed = (await Events(d)).Where(e => e.OriginNodeId == origin).ToArray();
        Assert.Equal(215, relayed.Count(e => e.Type == ReplicationEventType.MessageCreated));
        for (var offset = 0; offset < relayed.Length; offset += 100)
            await Distributed(b).ImportEventsAsync(new(relayed.Skip(offset).Take(100).ToArray()));
        var actual = await b.Store.GetMessagesAsync(conversation, limit: 500);
        Assert.Equal((await a.Store.GetMessagesAsync(conversation, limit: 500)).Select(x => x.Id), actual.Select(x => x.Id));
        var window = await Distributed(a).GetMessageWindowAsync(conversation, actual[100].Id);
        Assert.Equal(101, window.Length); Assert.Equal(actual[100].Id, window[50].Id);
    }
    [Fact]
    public async Task PersistedVotePreventsTwoCandidatesWinningTheSameTermAfterRestart()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32); var first = Guid.NewGuid(); var second = Guid.NewGuid(); string directory;
        await using (var voter = new TestNode(cluster, secret, "Voter"))
        {
            voter.Options.BootstrapNodeId = first; await voter.InitializeAsync(); directory = voter.Options.DataDirectory;
            using var db = voter.Host.Services.GetRequiredService<ChatDatabase>().Open();
            var config = new ControlCommand(Guid.NewGuid(), ControlCommandType.FinalMembership, Guid.Empty, Guid.Empty, NewVoters: [first, second, (await voter.Store.GetIdentityAsync()).NodeId]);
            db.Insert(new ControlLogEntry { Id = config.Id, Index = 1, Term = 1, CommandJson = System.Text.Json.JsonSerializer.Serialize(config) });
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
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32); var bootstrap = Guid.NewGuid();
        await using var a = new TestNode(cluster, secret, "A"); await using var b = new TestNode(cluster, secret, "B"); await using var c = new TestNode(cluster, secret, "C");
        a.Options.BootstrapNodeId = bootstrap; a.Options.InitialNodeId = bootstrap;
        await a.InitializeAsync(); await b.InitializeAsync(); await c.InitializeAsync();
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1"); await a.Store.RememberPeerAsync(await c.Store.GetNodeInfoAsync(), "127.0.0.1"); await a.Host.StartAsync();
        var service = a.Host.Services.GetRequiredService<IConsensusService>(); await TestNode.WaitUntilAsync(async () => (await service.GetStatusAsync()).HasQuorum);
        var owner = (await a.Store.GetIdentityAsync()).UserId; var userB = (await b.Store.GetIdentityAsync()).UserId; var userC = (await c.Store.GetIdentityAsync()).UserId; var group = Guid.NewGuid();
        async Task Apply(ControlCommandType type, string? name = null, Guid? target = null)
        {
            var result = await service.ProposeAsync(new(Guid.NewGuid(), type, owner, group, name, target, Members: [owner, userB])); Assert.True(result.Success, result.Error);
        }
        await Apply(ControlCommandType.GroupCreated, "Nhóm đầu tiên"); await Apply(ControlCommandType.GroupRenamed, "Tên đã đổi");
        Assert.False((await service.ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupAvatarChanged, owner, group, AvatarBase64: "aGVsbG8="))).Success); await Apply(ControlCommandType.GroupMemberAdded, target: userC); await Apply(ControlCommandType.GroupMemberRemoved, target: userB);
        var projected = Assert.Single(await Distributed(a).GetGroupsAsync()); Assert.Equal("Tên đã đổi", projected.Name); Assert.Contains(userC, projected.Members); Assert.DoesNotContain(userB, projected.Members);
        await Apply(ControlCommandType.GroupOwnerChanged, target: userC);
        Assert.Equal(userC, Assert.Single(await Distributed(a).GetGroupsAsync()).OwnerUserId);
        Assert.False((await service.ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupDeleted, owner, group))).Success);
        var second = Guid.NewGuid(); Assert.True((await service.ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupCreated, owner, second, "Xóa thử", Members: [owner]))).Success);
        Assert.True((await service.ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupDeleted, owner, second))).Success);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Distributed(a).SendToConversationAsync(second, "Nhóm đã xóa"));
    }

    [Fact]
    public async Task NominationAndWithdrawalKeepVotesAndReplicateQueuedMessages()
    {
        var cluster = Guid.NewGuid();
        var secret = FrameworkIO.RandomBytes(32);
        var bootstrap = Guid.NewGuid();
        await using var a = new TestNode(cluster, secret, "A");
        await using var b = new TestNode(cluster, secret, "B");
        await using var c = new TestNode(cluster, secret, "C");
        var nodes = new[] { a, b, c };
        a.Options.InitialNodeId = bootstrap;
        foreach (var node in nodes)
        {
            node.Options.BootstrapNodeId = bootstrap;
            node.Options.ReconciliationInterval = TimeSpan.FromHours(1);
            await node.InitializeAsync();
        }
        foreach (var node in nodes)
            foreach (var peer in nodes.Where(p => p != node))
                await node.Store.RememberPeerAsync(await peer.Store.GetNodeInfoAsync(), "127.0.0.1");
        foreach (var node in nodes) await node.Host.StartAsync();
        var services = nodes.Select(n => n.Host.Services.GetRequiredService<IConsensusService>()).ToArray();
        await TestNode.WaitUntilAsync(async () => (await Task.WhenAll(services.Select(s => s.GetStatusAsync())))
            .All(s => s.Voters.Length == 3 && s.JointVoters is null && s.LeaderReady), 30);

        var bId = await b.Store.GetIdentityAsync();
        var cId = await c.Store.GetIdentityAsync();
        var nomination = await services[2].NominateAsync(bId.NodeId);
        Assert.True(nomination.Success, nomination.Error);
        await TestNode.WaitUntilAsync(async () => (await services[1].GetStatusAsync()).HasQuorum);
        Assert.True((await services[0].SetCanLeadAsync(false)).Success);

        var conversation = await a.Store.OpenDirectAsync(cId.UserId);
        var faults = a.Host.Services.GetRequiredService<FaultInjection>();
        faults.PauseReplication = true;
        var message = await Distributed(a).SendToConversationAsync(conversation, "Đồng bộ ngay sau bàn giao");
        using (var db = a.Host.Services.GetRequiredService<ChatDatabase>().Open())
            db.Execute("UPDATE OutboxItems SET NextRetryTicks=$0 WHERE Status=0", DateTime.UtcNow.AddHours(1).Ticks);
        faults.PauseReplication = false;

        var withdrawal = await services[1].SetCanLeadAsync(false);
        Assert.True(withdrawal.Success, withdrawal.Error);
        var center = await services[2].GetStatusAsync();
        Assert.Equal(cId.NodeId, center.LeaderNodeId);
        Assert.True(center.HasQuorum);
        await TestNode.WaitUntilAsync(async () => (await c.Store.GetMessagesAsync(conversation)).Any(m => m.Id == message.Id), 4);

        await services[2].SetCanLeadAsync(false);
        await Task.Delay(5600);
        var withdrawn = await Task.WhenAll(services.Select(s => s.GetStatusAsync()));
        Assert.All(withdrawn, state => { Assert.False(state.CanLead); Assert.False(state.LeaderReady); });
        Assert.All(withdrawn, state => Assert.Equal(center.Term, state.Term));
        var queued = await Distributed(a).SendToConversationAsync(conversation, "Chat tiếp tục khi không có trung tâm");
        await TestNode.WaitUntilAsync(async () => (await c.Store.GetMessagesAsync(conversation)).Any(m => m.Id == queued.Id), 6);
        Assert.True((await services[2].SetCanLeadAsync(true)).Success);
        await TestNode.WaitUntilAsync(async () => (await services[2].GetStatusAsync()).HasQuorum, 8);
    }

    [Fact]
    public async Task TwoOnlineVotersCreateGroupAndRejoinQuorumWithoutChangingExistingGroups()
    {
        var cluster = Guid.NewGuid();
        var secret = FrameworkIO.RandomBytes(32);
        var bootstrap = Guid.NewGuid();
        await using var a = new TestNode(cluster, secret, "A");
        await using var b = new TestNode(cluster, secret, "B");
        await using var c = new TestNode(cluster, secret, "C");
        var nodes = new[] { a, b, c };
        a.Options.InitialNodeId = bootstrap;
        foreach (var node in nodes)
        {
            node.Options.BootstrapNodeId = bootstrap;
            node.Options.LeaderTimeout = TimeSpan.FromSeconds(2);
            await node.InitializeAsync();
        }

        var identities = await Task.WhenAll(nodes.Select(node => node.Store.GetIdentityAsync()));
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        await b.Store.RememberPeerAsync(await a.Store.GetNodeInfoAsync(), "127.0.0.1");
        foreach (var node in nodes.Take(2)) await c.Store.RememberPeerAsync(await node.Store.GetNodeInfoAsync(), "127.0.0.1");

        var voters = identities.Select(id => id.NodeId).Append(Guid.NewGuid()).Append(Guid.NewGuid()).ToArray();
        var config = new ControlCommand(Guid.NewGuid(), ControlCommandType.FinalMembership, Guid.Empty, Guid.Empty, NewVoters: voters);
        var previous = new ControlCommand(Guid.NewGuid(), ControlCommandType.GroupCreated, identities[0].UserId, Guid.NewGuid(),
            "Nhóm đã có", Members: [identities[0].UserId, identities[1].UserId]);
        previous = a.Host.Services.GetRequiredService<ChatStore>().SignGroupCommand(previous, bootstrap, CancellationToken.None);
        foreach (var node in nodes)
        {
            var service = node.Host.Services.GetRequiredService<ConsensusService>();
            var result = await service.AppendAsync(new(1, bootstrap, 0, 0,
                [new(config.Id, 1, 1, config), new(previous.Id, 2, 1, previous)], 2), bootstrap, CancellationToken.None);
            Assert.True(result.Success);
        }

        var services = nodes.Select(node => node.Host.Services.GetRequiredService<IConsensusService>()).ToArray();
        await a.Host.StartAsync();
        await b.Host.StartAsync();
        await TestNode.WaitUntilAsync(async () => (await Task.WhenAll(services.Take(2).Select(service => service.GetStatusAsync())))
            .All(status => status.PairGroupMemberUserId.HasValue));

        var pair = await Task.WhenAll(services.Take(2).Select(service => service.GetStatusAsync()));
        var center = Assert.Single(pair.Select(status => status.PairCoordinatorNodeId).Distinct());
        Assert.NotNull(center);
        Assert.All(pair, status => { Assert.False(status.LeaderReady); Assert.Equal(2, status.CommitIndex); });

        var rename = await services[0].ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupRenamed,
            identities[0].UserId, previous.GroupId, "Không được sửa"));
        Assert.False(rename.Success);
        var recreate = await services[0].ProposeAsync(previous with { Id = Guid.NewGuid(), Name = "Không được ghi đè" });
        Assert.False(recreate.Success);

        var creator = identities[0].NodeId == center ? 1 : 0;
        var recipient = 1 - creator;
        var fresh = new ControlCommand(Guid.NewGuid(), ControlCommandType.GroupCreated, identities[creator].UserId,
            Guid.NewGuid(), "Nhóm hai máy", Members: [identities[0].UserId, identities[1].UserId]);
        var created = await services[creator].ProposeAsync(fresh);
        Assert.True(created.Success, created.Error);
        Assert.True((await services[creator].ProposeAsync(fresh)).Success);
        await TestNode.WaitUntilAsync(async () => (await Distributed(nodes[recipient]).GetGroupsAsync())
            .Any(group => group.Id == fresh.GroupId && group.PendingCreation));

        var message = await Distributed(nodes[creator]).SendToConversationAsync(fresh.GroupId, "Tin nhắn từ nhóm hai máy");
        await TestNode.WaitUntilAsync(async () => (await nodes[recipient].Store.GetMessagesAsync(fresh.GroupId)).Any(m => m.Id == message.Id));
        Assert.Equal("Nhóm đã có", (await Distributed(a).GetGroupsAsync()).Single(group => group.Id == previous.GroupId).Name);

        foreach (var node in nodes.Take(2)) await node.Store.RememberPeerAsync(await c.Store.GetNodeInfoAsync(), "127.0.0.1");
        await c.Host.StartAsync();
        await TestNode.WaitUntilAsync(async () => (await Task.WhenAll(services.Select(service => service.GetStatusAsync())))
            .All(status => status.LeaderReady && status.PairCoordinatorNodeId is null), 25);
        await TestNode.WaitUntilAsync(async () => (await Task.WhenAll(nodes.Take(2).Select(node => Distributed(node).GetGroupsAsync())))
            .All(groups => groups.Any(group => group.Id == fresh.GroupId && !group.PendingCreation)), 20);
        var promotedRename = await services[creator].ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupRenamed,
            identities[creator].UserId, fresh.GroupId, "Nhóm đã xác nhận"));
        Assert.True(promotedRename.Success, promotedRename.Error);
        Assert.Contains(await nodes[recipient].Store.GetMessagesAsync(fresh.GroupId), m => m.Id == message.Id);
    }

    [Fact]
    public async Task LeaderLossStartsElectionAtFiveSecondsAndRecoversWithinSeven()
    {
        var cluster = Guid.NewGuid();
        var secret = FrameworkIO.RandomBytes(32);
        var bootstrap = Guid.NewGuid();
        await using var a = new TestNode(cluster, secret, "A");
        await using var b = new TestNode(cluster, secret, "B");
        await using var c = new TestNode(cluster, secret, "C");
        var nodes = new[] { a, b, c };
        a.Options.InitialNodeId = bootstrap;
        foreach (var node in nodes) { node.Options.BootstrapNodeId = bootstrap; await node.InitializeAsync(); }
        foreach (var node in nodes)
            foreach (var peer in nodes.Where(p => p != node))
                await node.Store.RememberPeerAsync(await peer.Store.GetNodeInfoAsync(), "127.0.0.1");
        foreach (var node in nodes) await node.Host.StartAsync();
        var services = nodes.Select(n => n.Host.Services.GetRequiredService<IConsensusService>()).ToArray();
        await TestNode.WaitUntilAsync(async () => (await Task.WhenAll(services.Select(s => s.GetStatusAsync())))
            .All(s => s.Voters.Length == 3 && s.JointVoters is null && s.LeaderReady), 30);
        a.Host.Services.GetRequiredService<FaultInjection>().SimulateLeaderFailure = true;
        var states = await Task.WhenAll(services.Skip(1).Select(s => s.GetStatusAsync()));
        var lastHeartbeat = states.Max(s => s.LastLeaderHeartbeatTicks);
        var term = states.Max(s => s.Term);
        long firstElection = 0;
        long leaderReady = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (leaderReady == 0)
        {
            var now = DateTime.UtcNow.Ticks;
            states = await Task.WhenAll(services.Skip(1).Select(s => s.GetStatusAsync(timeout.Token)));
            if (firstElection == 0 && states.Any(s => s.Term > term)) firstElection = now;
            if (states.Any(s => s.HasQuorum)) leaderReady = now;
            else await Task.Delay(25, timeout.Token);
        }
        Assert.InRange(TimeSpan.FromTicks(firstElection - lastHeartbeat).TotalSeconds, 4.9, 6.5);
        Assert.InRange(TimeSpan.FromTicks(leaderReady - lastHeartbeat).TotalSeconds, 5, 7);
    }
}
