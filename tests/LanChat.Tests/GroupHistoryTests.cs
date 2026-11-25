using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanChat.Tests;

public sealed class GroupHistoryTests
{
    private static IDistributedStore Distributed(TestNode node) => node.Host.Services.GetRequiredService<IDistributedStore>();

    private static async Task<EventDto[]> Events(TestNode node, Guid origin)
    {
        var events = new List<EventDto>();
        long after = 0;
        while (true)
        {
            var batch = await Distributed(node).GetEventRangeAsync(new(origin, after));
            if (batch.Events.Length == 0) return events.ToArray();
            events.AddRange(batch.Events);
            after = batch.Events.Last().OriginSequence;
        }
    }

    private static async Task Apply(TestNode owner, TestNode[] nodes, long index, ControlCommand command)
    {
        var identity = await owner.Store.GetIdentityAsync();
        if (command.Type >= ControlCommandType.GroupCreated)
            command = owner.Host.Services.GetRequiredService<ChatStore>().SignGroupCommand(command, identity.NodeId, default);
        foreach (var node in nodes)
        {
            var result = await node.Host.Services.GetRequiredService<ConsensusService>().AppendAsync(
                new(1, identity.NodeId, index - 1, index == 1 ? 0 : 1, [new(command.Id, index, 1, command)], index), identity.NodeId, default);
            Assert.True(result.Success);
        }
    }

    [Fact]
    public async Task GroupReactionCanReplicateBeforeQueuedDeliveryReceipts()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var sender = new TestNode(cluster, secret, "Sender");
        await using var member = new TestNode(cluster, secret, "Member");
        await sender.InitializeAsync(); await member.InitializeAsync();
        await sender.Store.RememberPeerAsync(await member.Store.GetNodeInfoAsync(), "127.0.0.1");
        await member.Store.RememberPeerAsync(await sender.Store.GetNodeInfoAsync(), "127.0.0.1");
        var owner = await sender.Store.GetIdentityAsync();
        var recipient = await member.Store.GetIdentityAsync();
        var group = Guid.NewGuid();
        var nodes = new[] { sender, member };
        await Apply(sender, nodes, 1, new(Guid.NewGuid(), ControlCommandType.FinalMembership, Guid.Empty, Guid.Empty, NewVoters: [owner.NodeId]));
        await Apply(sender, nodes, 2, new(Guid.NewGuid(), ControlCommandType.GroupCreated, owner.UserId, group, "Nhóm thử",
            Members: [owner.UserId, recipient.UserId]));

        var first = await Distributed(sender).SendToConversationAsync(group, "Tin đầu tiên");
        for (var i = 0; i < 20; i++)
            await Distributed(sender).SendToConversationAsync(group, "Tin lịch sử " + i);
        await Distributed(member).ImportEventsAsync(new(await Events(sender, owner.NodeId)));
        var queued = await Events(member, recipient.NodeId);
        Assert.Equal(21, queued.Count(e => e.Type == ReplicationEventType.ReceiptUpdated));

        await Distributed(member).ReactAsync(first.Id, "👍", true);
        var batch = await Distributed(member).GetOutboxAsync();
        Assert.Contains(first.Id, batch.Where(e => e.Type == ReplicationEventType.ReactionChanged).Select(e => e.EntityId));
        Assert.Contains(ReplicationEventType.ReceiptUpdated, batch.Select(e => e.Type));
        await Distributed(sender).ImportEventsAsync(new(batch));
        Assert.Contains("👍", (await Distributed(sender).GetMessageDetailsAsync(first.Id)).Reactions);
    }

    [Fact]
    public async Task OneRemainingHolderAutomaticallySharesAllPagesWithCurrentGroupMembers()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32); var bootstrap = Guid.NewGuid();
        await using var owner = new TestNode(cluster, secret, "Original sender");
        await using var holder = new TestNode(cluster, secret, "History holder");
        await using var c = new TestNode(cluster, secret, "Member C");
        await using var d = new TestNode(cluster, secret, "Member D");
        var nodes = new[] { owner, holder, c, d };
        owner.Options.InitialNodeId = bootstrap;
        foreach (var node in nodes) { node.Options.BootstrapNodeId = bootstrap; await node.InitializeAsync(); }
        foreach (var node in nodes)
            foreach (var peer in nodes.Where(peer => peer != node))
                await node.Store.RememberPeerAsync(await peer.Store.GetNodeInfoAsync(), "127.0.0.1");
        var ids = await Task.WhenAll(nodes.Select(node => node.Store.GetIdentityAsync()));
        var group = Guid.NewGuid();
        await Apply(owner, nodes, 1, new(Guid.NewGuid(), ControlCommandType.FinalMembership, Guid.Empty, Guid.Empty, NewVoters: [bootstrap]));
        await Apply(owner, nodes, 2, new(Guid.NewGuid(), ControlCommandType.GroupCreated, ids[0].UserId, group, "Lịch sử chung", Members: [ids[0].UserId, ids[1].UserId]));
        var ownerFiles = owner.Host.Services.GetRequiredService<IFileTransferService>();
        var filePath = Path.Combine(owner.Options.DataDirectory, "Lịch sử.txt");
        await FrameworkIO.WriteTextAsync(filePath, "Nội dung tệp lịch sử");
        var file = await ownerFiles.ImportLocalAsync(filePath);
        var folderPath = Path.Combine(Path.GetDirectoryName(owner.Options.DataDirectory)!, Guid.NewGuid().ToString("N"), "Thư mục lịch sử");
        Directory.CreateDirectory(Path.Combine(folderPath, "Rỗng"));
        await FrameworkIO.WriteTextAsync(Path.Combine(folderPath, "Báo cáo.txt"), "Nội dung thư mục lịch sử");
        var folder = await ownerFiles.ImportFolderAsync(folderPath);
        var messages = new List<Message>();
        for (var index = 0; index < 215; index++)
            messages.Add(await Distributed(owner).SendToConversationAsync(group, "Lịch sử nhóm số " + index,
                attachments: index == 0 ? [file, folder] : null));
        await Distributed(owner).EditMessageAsync(messages[0].Id, "Đã chỉnh sửa trong lịch sử nhóm");
        await Distributed(owner).RecallMessageAsync(messages[1].Id);
        var privateConversation = await owner.Store.OpenDirectAsync(ids[1].UserId);
        await owner.Store.SaveOutgoingAsync(ids[1].UserId, "PRIVATE GROUP HISTORY EXCLUSION");
        var history = await Events(owner, bootstrap);
        foreach (var node in nodes.Skip(1))
            for (var offset = 0; offset < history.Length; offset += 100)
                await Distributed(node).ImportEventsAsync(new(history.Skip(offset).Take(100).ToArray()));
        Assert.Equal(215, (await holder.Store.GetMessagesAsync(group, limit: 500)).Count(m => m.Kind != MessageKind.System));
        Assert.DoesNotContain(await c.Store.GetMessagesAsync(group), m => m.Kind != MessageKind.System);
        Assert.DoesNotContain(await d.Store.GetMessagesAsync(group), m => m.Kind != MessageKind.System);
        var listener = owner.Host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<PeerConnectionManager>().Single();
        await listener.StartAsync(default);
        try
        {
            foreach (var attachment in await Distributed(holder).GetAttachmentsAsync(group))
                await holder.Host.Services.GetRequiredService<IFileTransferService>().PrepareOpenAsync(attachment);
        }
        finally { await listener.StopAsync(default); }
        await Apply(owner, nodes, 3, new(Guid.NewGuid(), ControlCommandType.GroupMemberAdded, ids[0].UserId, group, TargetUserId: ids[2].UserId));
        await Apply(owner, nodes, 4, new(Guid.NewGuid(), ControlCommandType.GroupMemberAdded, ids[0].UserId, group, TargetUserId: ids[3].UserId));
        foreach (var node in nodes.Skip(1))
        {
            var faults = node.Host.Services.GetRequiredService<FaultInjection>();
            lock (faults.BlockedNodes) faults.BlockedNodes.Add(bootstrap);
            await node.Host.StartAsync();
        }
        await TestNode.WaitUntilAsync(async () => (await c.Store.GetMessagesAsync(group, limit: 500)).Count(m => m.Kind != MessageKind.System) == 215 &&
            (await d.Store.GetMessagesAsync(group, limit: 500)).Count(m => m.Kind != MessageKind.System) == 215, 35);
        foreach (var node in new[] { c, d })
        {
            var received = (await node.Store.GetMessagesAsync(group, limit: 500)).Where(m => m.Kind != MessageKind.System).ToArray();
            Assert.Equal(messages.Select(m => m.Id).OrderBy(id => id), received.Select(m => m.Id).OrderBy(id => id));
            Assert.Equal("Đã chỉnh sửa trong lịch sử nhóm", received.Single(m => m.Id == messages[0].Id).Text);
            Assert.True(received.Single(m => m.Id == messages[1].Id).IsRecalled);
            Assert.Empty(await node.Store.GetMessagesAsync(privateConversation));
            Assert.Empty(await Distributed(node).SearchMessagesAsync("PRIVATE GROUP HISTORY EXCLUSION"));
            var attachments = await Distributed(node).GetAttachmentsAsync(group);
            Assert.Equal(2, attachments.Length);
            var openedFile = Path.Combine(node.Options.DataDirectory, "Attachments", "Open", file.Id.ToString("N"), file.FileName);
            var openedFolder = Path.Combine(node.Options.DataDirectory, "Attachments", "Open", folder.Id.ToString("N"), folder.FileName);
            await TestNode.WaitUntilAsync(() => Task.FromResult(File.Exists(openedFile) && Directory.Exists(openedFolder)));
            Assert.Equal("Nội dung tệp lịch sử", await FrameworkIO.ReadTextAsync(openedFile));
            Assert.Equal("Nội dung thư mục lịch sử", await FrameworkIO.ReadTextAsync(Path.Combine(openedFolder, "Báo cáo.txt")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(openedFolder, "Rỗng")));
        }
        await Distributed(c).ReactAsync(messages[0].Id, "👍", true);
        // Peers may still be finishing the multi-page history round. Allow the
        // next full reconciliation interval before declaring the reaction lost.
        await TestNode.WaitUntilAsync(async () => (await Distributed(holder).GetMessageDetailsAsync(messages[0].Id)).Reactions.Contains("👍"), 35);
        await holder.Host.StopAsync();
        var later = await Distributed(c).SendToConversationAsync(group, "Tin tiếp tục từ thành viên vừa nhận lịch sử");
        await TestNode.WaitUntilAsync(async () => (await d.Store.GetMessagesAsync(group, limit: 500)).Any(m => m.Id == later.Id));
        await d.Host.StopAsync();
        using (var db = d.Host.Services.GetRequiredService<ChatDatabase>().Open())
        {
            db.Execute("DELETE FROM GroupHistoryKeys");
            db.Execute("DELETE FROM Messages WHERE ConversationId=$0 AND Kind<>$1", group, MessageKind.System);
        }
        await using var restored = new TestNode(cluster, secret, "Restored D", d.Options.DataDirectory);
        restored.Options.BootstrapNodeId = bootstrap;
        await restored.InitializeAsync();
        var restoredFaults = restored.Host.Services.GetRequiredService<FaultInjection>();
        lock (restoredFaults.BlockedNodes)
        {
            restoredFaults.BlockedNodes.Add(bootstrap);
            restoredFaults.BlockedNodes.Add(ids[1].NodeId);
        }
        await c.Store.RememberPeerAsync(await restored.Store.GetNodeInfoAsync(), "127.0.0.1");
        await restored.Host.StartAsync();
        await TestNode.WaitUntilAsync(async () => (await restored.Store.GetMessagesAsync(group, limit: 500)).Count(m => m.Kind != MessageKind.System) == 216, 35);
        Assert.True((await restored.Store.GetMessagesAsync(group, limit: 500)).Single(m => m.Id == messages[1].Id).IsRecalled);
    }

    [Fact]
    public async Task SharedHistoryRejectsTamperingAndExcludesPrivateMessagesAndNonMembers()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var owner = new TestNode(cluster, secret, "Owner");
        await using var member = new TestNode(cluster, secret, "Member");
        await using var newcomer = new TestNode(cluster, secret, "New member");
        await using var outsider = new TestNode(cluster, secret, "Other user");
        var nodes = new[] { owner, member, newcomer, outsider };
        foreach (var node in nodes) await node.InitializeAsync();
        foreach (var node in nodes)
            foreach (var peer in nodes.Where(peer => peer != node))
                await node.Store.RememberPeerAsync(await peer.Store.GetNodeInfoAsync(), "127.0.0.1");
        var ids = await Task.WhenAll(nodes.Select(node => node.Store.GetIdentityAsync()));
        var group = Guid.NewGuid();
        await Apply(owner, nodes, 1, new(Guid.NewGuid(), ControlCommandType.FinalMembership, Guid.Empty, Guid.Empty, NewVoters: [ids[0].NodeId]));
        await Apply(owner, nodes, 2, new(Guid.NewGuid(), ControlCommandType.GroupCreated, ids[0].UserId, group, "History", Members: [ids[0].UserId, ids[1].UserId]));
        var message = await Distributed(owner).SendToConversationAsync(group, "Lịch sử dành cho thành viên");
        var privateMessage = await owner.Store.SaveOutgoingAsync(ids[1].UserId, "PRIVATE HISTORY KEY EXCLUSION");
        var events = await Events(owner, ids[0].NodeId);
        var source = owner.Host.Services.GetRequiredService<ChatStore>();
        Assert.Empty((await source.AddGroupHistoryAsync(new(events), ids[2].NodeId, default)).GroupHistoryKeys);
        await Apply(owner, nodes, 3, new(Guid.NewGuid(), ControlCommandType.GroupMemberAdded, ids[0].UserId, group, TargetUserId: ids[2].UserId));
        var shared = await source.AddGroupHistoryAsync(new(events), ids[2].NodeId, default);
        var grant = Assert.Single(shared.GroupHistoryKeys);
        Assert.Equal(message.Id, events.Single(e => e.Id == grant.EventId).EntityId);
        Assert.Empty((await source.AddGroupHistoryAsync(new(events), ids[3].NodeId, default)).GroupHistoryKeys);
        var tampered = shared with { GroupHistoryKeys = [grant with { WrappedKey = Convert.ToBase64String(FrameworkIO.RandomBytes(384)) }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => Distributed(newcomer).ImportEventsAsync(tampered));
        await Distributed(newcomer).ImportEventsAsync(shared);
        await Distributed(newcomer).ImportEventsAsync(shared);
        Assert.Equal("Lịch sử dành cho thành viên", (await newcomer.Store.GetMessagesAsync(group)).Single(m => m.Id == message.Id).Text);
        Assert.Empty(await newcomer.Store.GetMessagesAsync(privateMessage.ConversationId));
        Assert.Equal(events, await Events(newcomer, ids[0].NodeId));
        await Apply(owner, nodes, 4, new(Guid.NewGuid(), ControlCommandType.GroupMemberRemoved, ids[0].UserId, group, TargetUserId: ids[2].UserId));
        var later = await Distributed(owner).SendToConversationAsync(group, "Tin sau khi rời nhóm");
        var excluded = await source.AddGroupHistoryAsync(new(await Events(owner, ids[0].NodeId)), ids[2].NodeId, default);
        Assert.Empty(excluded.GroupHistoryKeys);
        await Distributed(newcomer).ImportEventsAsync(excluded);
        Assert.DoesNotContain(await newcomer.Store.GetMessagesAsync(group), item => item.Id == later.Id);
    }

    [Fact]
    public async Task StartupRepairsStoredGroupEventsAndKeepsLocalDeletions()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var owner = new TestNode(cluster, secret, "Owner");
        await using var member = new TestNode(cluster, secret, "Member");
        var nodes = new[] { owner, member };
        foreach (var node in nodes) await node.InitializeAsync();
        await owner.Store.RememberPeerAsync(await member.Store.GetNodeInfoAsync(), "127.0.0.1");
        var ownerId = await owner.Store.GetIdentityAsync();
        var memberId = await member.Store.GetIdentityAsync();
        var group = Guid.NewGuid();
        await Apply(owner, nodes, 1, new(Guid.NewGuid(), ControlCommandType.FinalMembership, Guid.Empty, Guid.Empty, NewVoters: [ownerId.NodeId]));
        await Apply(owner, nodes, 2, new(Guid.NewGuid(), ControlCommandType.GroupCreated, ownerId.UserId, group, "Repair", Members: [ownerId.UserId, memberId.UserId]));
        var deleted = await Distributed(owner).SendToConversationAsync(group, "Xóa phía tôi");
        var missing = await Distributed(owner).SendToConversationAsync(group, "Khôi phục lịch sử nhóm đã lưu");
        await Distributed(member).ImportEventsAsync(new(await Events(owner, ownerId.NodeId)));
        await Distributed(member).DeleteLocalAsync(deleted.Id);
        using (var db = member.Host.Services.GetRequiredService<ChatDatabase>().Open()) db.Execute("DELETE FROM Messages WHERE Id=$0", missing.Id);
        await member.Host.StartAsync();
        await TestNode.WaitUntilAsync(async () => (await member.Store.GetMessagesAsync(group)).Any(item => item.Id == missing.Id));
        Assert.DoesNotContain(await member.Store.GetMessagesAsync(group), item => item.Id == deleted.Id);
        Assert.Single(await Distributed(member).SearchMessagesAsync("Khôi phục lịch sử nhóm"));
    }
}
