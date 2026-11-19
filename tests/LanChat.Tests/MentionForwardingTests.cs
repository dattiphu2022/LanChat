using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanChat.Tests;

public sealed class MentionForwardingTests
{
    private static IDistributedStore Distributed(TestNode node) => node.Host.Services.GetRequiredService<IDistributedStore>();
    private static async Task<EventBatch> Events(TestNode node) => await Distributed(node).GetEventRangeAsync(new((await node.Store.GetIdentityAsync()).NodeId, 0));
    private static async Task<Guid> Group(params TestNode[] nodes)
    {
        var infos = new List<NodeInfoDto>();
        foreach (var node in nodes) infos.Add(await node.Store.GetNodeInfoAsync());
        var group = Guid.NewGuid();
        foreach (var node in nodes)
        {
            var own = await node.Store.GetIdentityAsync();
            foreach (var info in infos.Where(i => i.User.Id != own.UserId)) await node.Store.RememberPeerAsync(info, "127.0.0.1");
            using var db = node.Host.Services.GetRequiredService<ChatDatabase>().Open();
            db.Execute("INSERT INTO Conversations(Id,Kind,IsPinned,IsMuted,LastReadTicks) VALUES ($0,1,0,0,0)", group);
            db.Execute("INSERT INTO Groups(Id,ConversationId,Name,OwnerUserId,IsDeleted) VALUES ($0,$0,'Nhóm kiểm thử',$1,0)", group, infos[0].User.Id);
            foreach (var user in infos.Select(i => i.User.Id))
            {
                db.Execute("INSERT INTO ConversationMembers VALUES ($0,$1,$2)", Guid.NewGuid(), group, user);
                db.Execute("INSERT INTO GroupMembers VALUES ($0,$1,$2)", Guid.NewGuid(), group, user);
            }
        }
        return group;
    }

    [Fact]
    public async Task MentionsAreEncryptedIdBasedSurviveRestartOverrideMuteAndDoNotReplayToasts()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "Lan"); await using var b = new TestNode(cluster, secret, "Tên trùng");
        await using var c = new TestNode(cluster, secret, "Tên trùng");
        await a.InitializeAsync(); await b.InitializeAsync(); await c.InitializeAsync();
        var group = await Group(a, b, c); var bId = (await b.Store.GetIdentityAsync()).UserId;
        await b.Store.SetConversationAsync(group, mute: true); await c.Store.SetConversationAsync(group, mute: true);
        var cursorB = await b.Store.GetIncomingMessagesAsync(null); var cursorC = await c.Store.GetIncomingMessagesAsync(null);
        const string raw = "  👋\r\n@Tên trùng xin chào  ";
        var message = await Distributed(a).SendToConversationAsync(group, raw, mentions: [new(raw.IndexOf('@'), "@Tên trùng".Length, false, [bId])]);
        var batch = await Events(a);
        Assert.DoesNotContain("@Tên trùng", batch.Events.Single(e => e.EntityId == message.Id).PayloadJson);
        await Distributed(b).ImportEventsAsync(batch); await Distributed(c).ImportEventsAsync(batch);
        var notice = Assert.Single((await b.Store.GetIncomingMessagesAsync(cursorB.Position)).Messages);
        Assert.True(notice.Mentioned); Assert.Equal(message.Id, notice.MessageId); Assert.Equal("Lan", notice.SenderName);
        Assert.Empty((await c.Store.GetIncomingMessagesAsync(cursorC.Position)).Messages);
        var mention = Assert.Single((await Distributed(b).GetMessageDetailsAsync(message.Id)).Mentions);
        Assert.Equal(raw.Trim().IndexOf('@'), mention.Start); Assert.Equal(bId, Assert.Single(mention.UserIds));
        Assert.Equal(1, Assert.Single(await b.Store.GetConversationsAsync()).UnreadMentionCount);
        var position = (await b.Store.GetIncomingMessagesAsync(cursorB.Position)).Position;
        await Distributed(b).ImportEventsAsync(batch);
        await Distributed(a).EditMessageAsync(message.Id, "@All đã sửa", mentions: [new(0, 4, true, [bId]) { ResolveAtSend = true }]);
        await Distributed(b).ImportEventsAsync(await Events(a));
        Assert.Empty((await b.Store.GetIncomingMessagesAsync(position)).Messages);
        await using var restarted = new TestNode(cluster, secret, "ignored", b.Options.DataDirectory); await restarted.InitializeAsync();
        Assert.Equal(3, Assert.Single((await Distributed(restarted).GetMessageDetailsAsync(message.Id)).Mentions).UserIds.Length);
        Assert.Empty((await restarted.Store.GetIncomingMessagesAsync(null)).Messages);
        await restarted.Store.SetConversationAsync(group, markRead: true);
        Assert.Equal(0, Assert.Single(await restarted.Store.GetConversationsAsync()).UnreadMentionCount);
        await Distributed(a).RecallMessageAsync(message.Id); await Distributed(restarted).ImportEventsAsync(await Events(a));
        Assert.Empty((await Distributed(restarted).GetMessageDetailsAsync(message.Id)).Mentions);
    }

    [Fact]
    public void MentionOffsetsTrackEditsUndoUnicodeAndPlainTextDoesNotCreateTargets()
    {
        var user = Guid.NewGuid(); var draft = new MentionDraft();
        draft.Reset("👋 @Hà\r\ncuối", [new(3, 3, false, [user])]);
        draft.Change("A 👋 @Hà\r\ncuối"); Assert.Equal(5, Assert.Single(draft.Values).Start);
        draft.Change("A 👋 @Hải\r\ncuối"); Assert.Empty(draft.Values);
        draft.Change("A 👋 @Hà\r\ncuối", undo: true); Assert.Equal(user, Assert.Single(Assert.Single(draft.Values).UserIds));
        draft.Reset("", []); draft.Change("@Hà email@example.test"); Assert.Empty(draft.Values);
        Assert.True(MentionDraft.Matches("Đặng Thị Hà", "dang thi ha"));
    }

    [Fact]
    public void EditingAnExistingAllDoesNotAddFutureMembersButNewAllResolvesAtCommit()
    {
        var old = Guid.NewGuid(); var joined = Guid.NewGuid();
        var existing = new MessageMention(0, 4, true, [old]);
        var unchanged = Mentions.Normalize("@All sửa chữ", [existing], [old, joined], true, trim: true, resolveAll: false);
        Assert.Equal(old, Assert.Single(Assert.Single(unchanged).UserIds));
        var fresh = Mentions.Normalize("@All sửa chữ", [existing with { ResolveAtSend = true }], [old, joined], true, trim: true, resolveAll: false);
        Assert.Equal(2, Assert.Single(fresh).UserIds.Length); Assert.False(Assert.Single(fresh).ResolveAtSend);
        Assert.Throws<ArgumentException>(() => Mentions.Normalize("@Tên", [new(0, 4, false, [old])], [joined], true, trim: true));
    }

    [Fact]
    public async Task BatchForwardsInOrderToNewContactsWithIndependentAttachmentsAndClearsMentionMetadata()
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var a = new TestNode(cluster, secret, "Lan"); await using var b = new TestNode(cluster, secret, "Hà"); await using var c = new TestNode(cluster, secret, "Hà");
        await a.InitializeAsync(); await b.InitializeAsync(); await c.InitializeAsync();
        var group = await Group(a, b, c); var bId = (await b.Store.GetIdentityAsync()).UserId; var cId = (await c.Store.GetIdentityAsync()).UserId;
        var files = a.Host.Services.GetRequiredService<IFileTransferService>();
        var source = Path.Combine(a.Options.DataDirectory, "abc.z.exe"); File.WriteAllText(source, "immutable original");
        var attachment = await files.ImportLocalAsync(source); File.WriteAllText(await files.PrepareOpenAsync(attachment), "local edit");
        var first = await Distributed(a).SendToConversationAsync(group, "@Hà đầu", attachments: [attachment], mentions: [new(0, 3, false, [bId])]);
        var second = await Distributed(a).SendToConversationAsync(group, new string('x', 700));
        var targets = new[] { new ForwardDestination(bId, false, "Hà"), new ForwardDestination(cId, false, "Hà") };
        var requests = new[] { second.Id, first.Id }.SelectMany(id => targets.Select(target => new ForwardRequest(id, target))).ToArray();
        var results = await a.Host.Services.GetRequiredService<IChatService>().ForwardBatchAsync(requests);
        Assert.Equal(4, results.Length); Assert.All(results, r => Assert.Equal(ForwardOutcome.Queued, r.Outcome));
        var ids = new List<Guid>(); var keys = new List<string?>();
        foreach (var target in targets)
        {
            var conversation = await a.Store.OpenDirectAsync(target.Id);
            var messages = await a.Store.GetMessagesAsync(conversation);
            Assert.Equal(new[] { first.Text, second.Text }, messages.Select(m => m.Text));
            var details = await Distributed(a).GetMessageDetailsAsync(messages[0].Id); Assert.Empty(details.Mentions);
            var forwarded = Assert.Single(details.Attachments); ids.Add(forwarded.Id); keys.Add(forwarded.FileKey);
            Assert.Equal("immutable original", File.ReadAllText(await files.PrepareOpenAsync(forwarded)));
        }
        Assert.Equal(2, ids.Distinct().Count()); Assert.Equal(2, keys.Distinct().Count());
        using var cancel = new CancellationTokenSource();
        var completed = 0;
        var canceled = await a.Host.Services.GetRequiredService<IChatService>().ForwardBatchAsync(requests,
            new ImmediateProgress<ForwardProgress>(p => { if (p.Completed > completed) { completed = p.Completed; cancel.Cancel(); } }), cancel.Token);
        Assert.Single(canceled, r => r.Outcome == ForwardOutcome.Queued);
        Assert.Equal(3, canceled.Count(r => r.Outcome == ForwardOutcome.Cancelled));
    }

    [Fact]
    public async Task LocationReadDoesNotWaitForWriterAndPreparationIsSharedWhenOneSubscriberCancels()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = node.Host.Services.GetRequiredService<PeerFileTransferService>();
        var source = Path.Combine(node.Options.DataDirectory, "file.txt"); File.WriteAllText(source, "original"); var file = await service.ImportLocalAsync(source);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var preparations = 0;
        service.PreparationCheckpoint = async ct => { Interlocked.Increment(ref preparations); entered.TrySetResult(true); await release.Task; ct.ThrowIfCancellationRequested(); };
        var background = service.PrepareOpenAsync(file); await entered.Task;
        using var cancel = new CancellationTokenSource(); var foreground = service.PrepareOpenAsync(file, ct: cancel.Token);
        var location = service.GetLocalLocationAsync(file); Assert.Same(location, await Task.WhenAny(location, Task.Delay(1000)));
        Assert.False((await location).Exists); Assert.NotNull(service.GetPreparationState(file.Id));
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreground);
        Assert.False(background.IsCompleted); release.SetResult(true); Assert.True(File.Exists(await background)); Assert.Equal(1, preparations);
        Assert.Equal(TimeSpan.FromSeconds(30), PeerFileTransferService.SourceResponseTimeout(0));
        Assert.Equal(TimeSpan.FromMinutes(5), PeerFileTransferService.SourceResponseTimeout(20L * 1024 * 1024 * 1024));
    }
    private sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }

    [Fact]
    public async Task LastCancellationDrainsWriterAndRetryCannotJoinAbandonedOperation()
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "A"); await node.InitializeAsync();
        var service = node.Host.Services.GetRequiredService<PeerFileTransferService>();
        var source = Path.Combine(node.Options.DataDirectory, "retry.txt"); File.WriteAllText(source, "original"); var file = await service.ImportLocalAsync(source);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        service.PreparationCheckpoint = async ct =>
        {
            if (Interlocked.Increment(ref count) != 1) return;
            entered.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { canceled.TrySetResult(true); await stopped.Task; ct.ThrowIfCancellationRequested(); }
        };
        using var cancellation = new CancellationTokenSource();
        var first = service.PrepareOpenAsync(file, ct: cancellation.Token); await entered.Task;
        cancellation.Cancel(); await canceled.Task;
        var retry = service.PrepareOpenAsync(file);
        Assert.False(first.IsCompleted); Assert.False(retry.IsCompleted); Assert.Equal(1, count);
        stopped.SetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.True(File.Exists(await retry)); Assert.Equal(2, count);
    }
}
