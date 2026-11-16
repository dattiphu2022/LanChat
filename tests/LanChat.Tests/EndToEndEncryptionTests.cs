using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanChat.Tests;

public sealed class EndToEndEncryptionTests
{
    [Fact]
    public async Task DeviceKeyIsPinnedAndFingerprintSurvivesRestart()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var b = new TestNode(cluster, secret, "Người nhận");
        await b.InitializeAsync();
        var peer = await b.Store.GetNodeInfoAsync();
        string directory, fingerprint;
        await using (var a = new TestNode(cluster, secret, "Người gửi"))
        {
            await a.InitializeAsync(); directory = a.Options.DataDirectory;
            await a.Store.RememberPeerAsync(peer, "127.0.0.1");
            fingerprint = Assert.Single(await a.Store.GetEncryptionIdentitiesAsync(), x => !x.IsLocal).Fingerprint;
            Assert.Equal(Assert.Single(await b.Store.GetEncryptionIdentitiesAsync()).Fingerprint, fingerprint);
        }

        await using var impostor = new TestNode(cluster, secret, "Tên trùng");
        impostor.Options.InitialNodeId = peer.NodeId;
        await impostor.InitializeAsync();
        await using var restarted = new TestNode(cluster, secret, "Người gửi", directory);
        await restarted.InitializeAsync();
        var replacement = await impostor.Store.GetNodeInfoAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.Store.RememberPeerAsync(replacement, "127.0.0.2"));
        Assert.Equal(fingerprint, Assert.Single(await restarted.Store.GetEncryptionIdentitiesAsync(), x => !x.IsLocal).Fingerprint);
        Assert.Equal(peer.User.Id, Assert.Single(await restarted.Store.GetUsersAsync()).UserId);
    }

    [Fact]
    public async Task RemovingGroupMemberExcludesTheirKeyFromNewMessagesAndEdits()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "Chủ nhóm");
        await using var b = new TestNode(cluster, secret, "Thành viên");
        await using var c = new TestNode(cluster, secret, "Thành viên cũ");
        a.Options.InitialNodeId = a.Options.BootstrapNodeId = Guid.NewGuid();
        await b.InitializeAsync(); await c.InitializeAsync(); await a.InitializeAsync(true);
        var owner = await a.Store.GetIdentityAsync();
        var member = await b.Store.GetNodeInfoAsync();
        var removed = await c.Store.GetNodeInfoAsync();
        await a.Store.RememberPeerAsync(member, "127.0.0.1");
        await a.Store.RememberPeerAsync(removed, "127.0.0.1");
        var consensus = a.Host.Services.GetRequiredService<IConsensusService>();
        var sender = a.Host.Services.GetRequiredService<IDistributedStore>();
        var group = Guid.NewGuid();
        await TestNode.WaitUntilAsync(async () => (await consensus.GetStatusAsync()).HasQuorum);
        var created = await consensus.ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupCreated, owner.UserId, group,
            "Nhóm kiểm tra khóa", Members: [owner.UserId, member.User.Id, removed.User.Id]));
        Assert.True(created.Success, created.Error);
        var first = await sender.SendToConversationAsync(group, "Before removal");
        var change = await consensus.ProposeAsync(new(Guid.NewGuid(), ControlCommandType.GroupMemberRemoved,
            owner.UserId, group, TargetUserId: removed.User.Id));
        Assert.True(change.Success, change.Error);
        await sender.EditMessageAsync(first.Id, "Private edit after removal");
        var last = await sender.SendToConversationAsync(group, "Private message after removal");

        var batch = await sender.GetEventRangeAsync(new(owner.NodeId, 0));
        var protectedEvents = batch.Events.Where(e => e.EntityId == last.Id || e.Type == ReplicationEventType.MessageEdited);
        foreach (var item in protectedEvents)
            Assert.DoesNotContain(DeviceIdentity.ParseEnvelope(item.PayloadJson).Recipients, x => x.UserId == removed.User.Id);
        foreach (var peer in new[] { b, c }) await peer.Host.Services.GetRequiredService<IDistributedStore>().ImportEventsAsync(batch);
        Assert.Equal(new[] { "Private edit after removal", last.Text }, (await b.Store.GetMessagesAsync(group)).Select(x => x.Text));
        Assert.Equal("Before removal", Assert.Single(await c.Store.GetMessagesAsync(group)).Text);
    }

    [Fact]
    public async Task LegacyGroupIsRestoredByItsOwnerWithFreshSignedControlLog()
    {
        var cluster = Guid.NewGuid();
        await using var owner = new TestNode(cluster, FrameworkIO.RandomBytes(32), "Chủ nhóm");
        var nodeId = Guid.NewGuid(); var user = Guid.NewGuid(); var groupId = Guid.NewGuid();
        owner.Options.BootstrapNodeId = nodeId;
        var database = owner.Host.Services.GetRequiredService<ChatDatabase>();
        database.Migrate("20261116100000_PeerAddresses");
        using (var db = database.Open())
        {
            db.Execute("INSERT INTO Users(Id,DisplayName) VALUES ($0,'Chủ nhóm')", user);
            db.Execute("INSERT INTO LocalSettings(Id,UserId,NodeId,CreatedAtTicks,LastSequence,PhysicalTicks,LogicalCounter,ClusterId) VALUES ($0,$1,$2,$3,0,0,0,$4)",
                Guid.NewGuid(), user, nodeId, DateTime.UtcNow.Ticks, cluster);
            db.Execute("INSERT INTO Conversations(Id,Kind,IsPinned,IsMuted,LastReadTicks,Title) VALUES ($0,1,1,0,0,'Nhóm cũ')", groupId);
            db.Execute("INSERT INTO Groups(Id,ConversationId,Name,OwnerUserId,IsDeleted) VALUES ($0,$0,'Nhóm cũ',$1,0)", groupId, user);
            db.Execute("INSERT INTO GroupMembers VALUES ($0,$1,$2)", Guid.NewGuid(), groupId, user);
        }

        await owner.InitializeAsync(true);
        var store = owner.Host.Services.GetRequiredService<IDistributedStore>();
        await TestNode.WaitUntilAsync(async () => (await store.GetGroupsAsync()).Any(g => g.Id == groupId));
        var restored = Assert.Single(await store.GetGroupsAsync());
        Assert.Equal("Nhóm cũ", restored.Name);
        Assert.Equal(user, Assert.Single(restored.Members));
        Assert.True(Assert.Single(await owner.Store.GetConversationsAsync()).IsPinned);
        using var current = database.Open();
        var group = current.Required<Group>("Id=$0", groupId);
        var command = current.Required<ControlLogEntry>("Id=$0", group.LastCommandId);
        Assert.False(string.IsNullOrEmpty(System.Text.Json.JsonSerializer.Deserialize<ControlCommand>(command.CommandJson)!.Signature));
    }

    [Fact]
    public async Task AttachmentMetadataCannotRegisterASecondTransferKeyForAnExistingLocalBlob()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "A");
        await using var b = new TestNode(cluster, secret, "B");
        await a.InitializeAsync(); await b.InitializeAsync();
        await a.Store.RememberPeerAsync(await b.Store.GetNodeInfoAsync(), "127.0.0.1");
        var path = Path.Combine(b.Options.DataDirectory, "private.txt");
        await FrameworkIO.WriteTextAsync(path, "Local private attachment");
        var files = b.Host.Services.GetRequiredService<IFileTransferService>();
        var local = await files.ImportLocalAsync(path);
        var unrelatedKey = FrameworkIO.RandomBytes(DeviceIdentity.KeyBytes);
        var reference = local with { Id = Guid.NewGuid(), FileKey = Convert.ToBase64String(unrelatedKey) };
        var conversation = await a.Store.OpenDirectAsync((await b.Store.GetIdentityAsync()).UserId);
        var sender = a.Host.Services.GetRequiredService<IDistributedStore>();
        await sender.SendToConversationAsync(conversation, "Tham chiếu tới tệp", attachments: [reference]);
        var recipient = b.Host.Services.GetRequiredService<ChatStore>();
        await recipient.ImportEventsAsync(await sender.GetEventRangeAsync(new((await a.Store.GetIdentityAsync()).NodeId, 0)));
        var projected = Assert.Single(await recipient.GetAttachmentsAsync(conversation));
        Assert.Equal(local.Sha256, projected.Sha256);
        Assert.Null(recipient.FindFileKey(FileTransferCrypto.TransferId(unrelatedKey), default));
        await files.DownloadAsync(projected);
        Assert.Null(recipient.FindFileKey(FileTransferCrypto.TransferId(unrelatedKey), default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(140_000)]
    public async Task FileStreamAuthenticatesEveryChunkIncludingEmptyFiles(int length)
    {
        var data = FrameworkIO.RandomBytes(length);
        var key = FrameworkIO.RandomBytes(DeviceIdentity.KeyBytes);
        var hash = FrameworkIO.Hex(FrameworkIO.Hash(data));
        using var original = new MemoryStream(data);
        using var wire = new MemoryStream();
        await FileTransferCrypto.WriteAsync(original, wire, length, hash, key, default);
        var ciphertext = wire.ToArray();
        Assert.NotEqual(data, ciphertext);
        wire.Position = 0;
        using var recovered = new MemoryStream();
        await FileTransferCrypto.ReadAsync(wire, recovered, length, hash, key, null, () => { }, default);
        Assert.Equal(data, recovered.ToArray());
        wire.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => FileTransferCrypto.ReadAsync(wire, Stream.Null, length, hash,
            FrameworkIO.RandomBytes(DeviceIdentity.KeyBytes), null, () => { }, default));
        ciphertext[ciphertext.Length - 1] ^= 0x01;
        using var corrupted = new MemoryStream(ciphertext);
        await Assert.ThrowsAsync<InvalidDataException>(() => FileTransferCrypto.ReadAsync(corrupted, Stream.Null, length, hash, key, null, () => { }, default));
    }
}
