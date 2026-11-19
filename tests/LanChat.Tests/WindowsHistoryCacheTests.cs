using System.Reflection;
using System.Windows.Forms;
using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using LanChat.WinForms.Forms;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ChatMessage = LanChat.Domain.Message;

namespace LanChat.Tests;

public sealed partial class WindowsUiTests
{
    [Fact]
    public Task SwitchingHistoryKeepsNativeRowsWarmWithoutDuplicateMessages() => OnUi(async host =>
    {
        using var list = new MessageListControl(); host.Controls.Add(list);
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var user = Guid.NewGuid();
        var messages = Enumerable.Range(0, 12).Select(i => new ChatMessage { Id = Guid.NewGuid(),
            ConversationId = a, SenderUserId = user, Text = "Tin " + i, CreatedAtTicks = DateTime.UtcNow.Ticks }).ToArray();
        await list.PrepareAsync(messages.Length, default);
        list.Bind(a, messages, user, []);
        var initial = Children(list).OfType<MessageBubbleControl>().ToArray();
        for (var i = 0; i < 3; i++)
        {
            list.Bind(b, [], user, []); list.Bind(a, messages, user, []);
            Assert.Equal(12, list.MessageCount);
            Assert.All(initial, bubble => Assert.False(bubble.IsDisposed));
            Assert.Equal(12, Children(list).OfType<MessageBubbleControl>().Count());
        }
    });

    [Fact]
    public Task ReopeningHistoryReusesSnapshotAndRefreshesAfterNewMessages() => OnUi(async host =>
    {
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var local = new TestNode(cluster, secret, "Local");
        await using var peer = new TestNode(cluster, secret, "Peer");
        await local.InitializeAsync(); await peer.InitializeAsync();
        var remote = await peer.Store.GetNodeInfoAsync();
        await local.Store.RememberPeerAsync(remote, "127.0.0.1");
        var a = await local.Store.OpenDirectAsync(remote.User.Id);
        await local.Store.SaveOutgoingAsync(remote.User.Id, "Lịch sử đã mở");
        // A second peer gives us another conversation to switch through.
        await using var other = new TestNode(cluster, secret, "Other"); await other.InitializeAsync();
        var remoteOther = await other.Store.GetNodeInfoAsync();
        await local.Store.RememberPeerAsync(remoteOther, "127.0.0.2");
        var b = await local.Store.OpenDirectAsync(remoteOther.User.Id);
        using var view = ActivatorUtilities.CreateInstance<MainForm>(local.Host.Services,
            new ChatImageService(local.Host.Services.GetRequiredService<IFileTransferService>()));
        view.EmbedInWorkspace((_, _, _, _) => { }, () => true);
        host.Controls.Add(view); view.Show(); await view.StartViewAsync();
        Field<System.Windows.Forms.Timer>(view, "_timer").Stop();
        await view.SelectConversationAsync(a);
        var first = Field<ChatMessage[]>(view, "_visibleMessages");
        await view.SelectConversationAsync(b); await view.SelectConversationAsync(a);
        Assert.Same(first, Field<ChatMessage[]>(view, "_visibleMessages"));
        Assert.Equal("Lịch sử đã mở", Assert.Single(first).Text);
        await view.SelectConversationAsync(b);
        await local.Store.SaveOutgoingAsync(remote.User.Id, "Tin mới khi đang xem hội thoại khác");
        await view.RefreshAsync(); // global revision advances while A is inactive
        await view.SelectConversationAsync(a);
        Assert.Equal(2, Field<ChatMessage[]>(view, "_visibleMessages").Length);
        Assert.NotSame(first, Field<ChatMessage[]>(view, "_visibleMessages"));
        await local.Store.DeleteConversationLocalAsync(a); await view.RefreshAsync();
        Assert.Empty(Field<ChatMessage[]>(view, "_visibleMessages"));
    });

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
