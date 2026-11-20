using System.Drawing;
using System.Windows.Forms;
using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;
using LanChat.WinForms.Forms;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanChat.Tests;

public sealed partial class WindowsUiTests
{
    [Fact]
    public Task ConversationFiltersPreserveQueryAndReuseUnchangedRows() => OnUi(host =>
    {
        using var list = new ConversationListControl(); host.Controls.Add(list);
        var a = new ConversationSummary(Guid.NewGuid(), Guid.NewGuid(), "Chưa đọc", "Nội dung", 1, 1, true, false, PresenceStatus.Online) { UnreadMentionCount = 1 };
        var b = new ConversationSummary(Guid.NewGuid(), Guid.NewGuid(), "Đã đọc", "Khác", 1, 0, false, true, PresenceStatus.Offline);
        list.Bind([a,b],a.Id); var rows = Children(list).OfType<ConversationItemControl>().ToArray();
        list.Bind([a,b],a.Id); Assert.Equal(rows, Children(list).OfType<ConversationItemControl>().ToArray());
        foreach (var filter in new[] { "Chưa đọc", "@ Tôi", "Đã ghim" })
        {
            Button(list, filter).PerformClick(); Assert.Single(Children(list).OfType<ConversationItemControl>());
            list.Bind([a,b],a.Id); Assert.Single(Children(list).OfType<ConversationItemControl>());
        }
        list.SetActive(b.Id);
        Button(list,"Tất cả").PerformClick(); Assert.Equal(2, Children(list).OfType<ConversationItemControl>().Count());
        Assert.Contains(Children(list).OfType<ConversationItemControl>(),row => row.AccessibleName!.StartsWith("Đã đọc") && row.BackColor == Theme.Selection);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("Nguyễn Minh Anh", "nguyen", 0)]
    [InlineData("Báo cáo Đinh", "dinh", 8)]
    [InlineData("Nội dung", "khác", -1)]
    public void SearchSnippetsHighlightVietnameseQueriesWithoutAccents(string text, string query, int expected) =>
        Assert.Equal(expected, SearchResultListBox.KeywordIndex(text, query));

    [Fact]
    public void MessageDeliveryNeverInfersRecipientReceiptFromReplication()
    {
        var message = new LanChat.Domain.Message { Status = MessageStatus.LocalPending };
        Assert.Contains("chờ đồng bộ", MessageDeliveryPresentation.For(message,0).Text);
        message.Status = MessageStatus.Synced;
        Assert.Contains("Đã đồng bộ", MessageDeliveryPresentation.For(message,0).Text);
        Assert.DoesNotContain("Đã nhận", MessageDeliveryPresentation.For(message,0).Text);
        message.Status = MessageStatus.Delivered;
        Assert.Contains("Đã nhận", MessageDeliveryPresentation.For(message,0).Text);
        Assert.Contains("Đã đọc", MessageDeliveryPresentation.For(message,1).Text);
    }

    [Theory]
    [InlineData(96)] [InlineData(120)] [InlineData(144)] [InlineData(192)]
    public async Task ResponsiveWorkspaceKeepsDraftAndConversationAtEverySize(int dpi)
    {
        await using var node = new TestNode(Guid.NewGuid(), FrameworkIO.RandomBytes(32), "Thử giao diện"); await node.InitializeAsync();
        await using var peer = new TestNode(node.Options.ClusterId, node.Options.ClusterSecret, "Đồng nghiệp"); await peer.InitializeAsync();
        var remote = await peer.Store.GetNodeInfoAsync(); await node.Store.RememberPeerAsync(remote,"127.0.0.1");
        var id = await node.Store.OpenDirectAsync(remote.User.Id); await node.Store.SaveOutgoingAsync(remote.User.Id,"Tin kiểm tra bố cục");
        await OnUi(async host =>
        {
            using var view = ActivatorUtilities.CreateInstance<MainForm>(node.Host.Services, new ChatImageService(node.Host.Services.GetRequiredService<IFileTransferService>()));
            var notifications = new List<string>();
            view.EmbedInWorkspace((_,_,_,body)=>notifications.Add(body),()=>true); view.Dock = DockStyle.None; host.Controls.Add(view); view.Show(); await view.StartViewAsync();
            Field<System.Windows.Forms.Timer>(view,"_timer").Stop();
            await node.Store.SaveOutgoingAsync(remote.User.Id,"Tin kiểm tra trạng thái được cập nhật ngay");
            await view.RefreshAsync();
            Assert.Contains("2 tin chờ đồng bộ",Field<ToolStripStatusLabel>(view,"_status").Text);
            view.ApplyPreferences(new LanChat.WinForms.UiPreferences(HideNotificationContent:true));
            typeof(MainForm).GetMethod("NotifyIncoming",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(view,
                new object[] { new[] { new IncomingMessageNotice(id,"Nội dung bí mật") { MessageId=Guid.NewGuid(),Mentioned=true,SenderName="Tên nhạy cảm" } } });
            Assert.Contains("nhắc tới bạn",Assert.Single(notifications));
            Assert.DoesNotContain("bí mật",notifications[0]); Assert.DoesNotContain("nhạy cảm",notifications[0]);
            view.ApplyPreferences(new());
            using var fonts = SimulateDpi(host,dpi);
            await view.SelectConversationAsync(id);
            var chat = Field<ChatViewControl>(view,"_chat"); chat.RestoreDraft(new ComposerDraft("Giữ bản nháp",[],null,null,""));
            foreach (var size in new[] { new Size(660,450),new Size(800,600),new Size(1024,768),new Size(1280,800),new Size(1920,1080) })
            {
                view.ClientSize = new Size(size.Width*dpi/96,size.Height*dpi/96); await Task.Delay(40);
                Assert.Equal(dpi, view.LayoutDpi());
                Assert.Equal(id,Field<Guid?>(view,"_activeConversation")); Assert.Equal("Giữ bản nháp",chat.Draft().Text);
                Assert.True(chat.Visible);
                foreach(var filterButton in Children(Field<ConversationListControl>(view,"_conversations")).OfType<Button>())
                    Assert.InRange(filterButton.Height, 1, view.ScalePixels(38));
                var navigation = Children(view).OfType<NavigationItemControl>().Where(n=>n.Visible).ToArray();
                for(var navIndex=0;navIndex<navigation.Length;navIndex++)
                    foreach(var otherNav in navigation.Skip(navIndex+1))
                    { var overlap=Rectangle.Intersect(navigation[navIndex].Bounds,otherNav.Bounds); Assert.True(overlap.Width<=0 || overlap.Height<=0, "Navigation items overlap"); }
                var list = Field<Panel>(view,"_conversationColumn"); Assert.True((size.Width>=800)==list.Visible, $"dpi={dpi}; size={size}; view={view.ClientSize}; workspace={Field<TableLayoutPanel>(view, "_workspace").ClientSize}; layoutDpi={view.LayoutDpi()}; hostDpi={host.DeviceDpi}; viewDpi={view.DeviceDpi}");
                foreach (var button in Children(chat).OfType<Button>().Where(b=>b.Visible && b.Parent is not FlowLayoutPanel))
                { Assert.True(button.Width>0); Assert.True(button.Height>0); }
                var output=Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
                if (!string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(output); using var bitmap=new Bitmap(view.Width,view.Height);
                    view.DrawToBitmap(bitmap,new Rectangle(Point.Empty,view.Size)); bitmap.Save(Path.Combine(output,$"chat-{size.Width}-{dpi}.png"));
                }
                if (size.Width<800)
                {
                    var back = Children(chat).OfType<Button>().Single(b=>b.AccessibleName == "Quay lại danh sách · Alt+Trái");
                    back.PerformClick(); Assert.True(list.Visible); Assert.False(chat.Visible);
                    await view.SelectConversationAsync(id); Assert.True(chat.Visible); Assert.Equal("Giữ bản nháp",chat.Draft().Text);
                }
            }
        });
    }
}
