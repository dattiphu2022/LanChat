using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;
using LanChat.WinForms.Forms;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LanChat.Tests;

public sealed partial class WindowsUiTests
{
    [Fact]
    public async Task CaptureResponsiveWorkspaceAndMeasureConversationSwitches()
    {
        var output = Environment.GetEnvironmentVariable("LANCHAT_UX_EVIDENCE");
        if (string.IsNullOrEmpty(output)) return; // Opt-in benchmark, same fixture before and after.
        Directory.CreateDirectory(output);
        var cluster = Guid.NewGuid(); var secret = FrameworkIO.RandomBytes(32);
        await using var local = new TestNode(cluster, secret, "Người thử nghiệm");
        await using var peer = new TestNode(cluster, secret, "Nguyễn Minh Anh");
        await using var other = new TestNode(cluster, secret, "Trần Thanh Bình");
        await local.InitializeAsync(); await peer.InitializeAsync(); await other.InitializeAsync();
        var remote = await peer.Store.GetNodeInfoAsync(); var second = await other.Store.GetNodeInfoAsync();
        await local.Store.RememberPeerAsync(remote, "127.0.0.1"); await local.Store.RememberPeerAsync(second, "127.0.0.2");
        var a = await local.Store.OpenDirectAsync(remote.User.Id); var b = await local.Store.OpenDirectAsync(second.User.Id);
        for (var i = 0; i < 24; i++)
        {
            await local.Store.SaveOutgoingAsync(remote.User.Id, "Trao đổi công việc số " + i + " · Giữ bản nháp và lịch sử hội thoại.");
            await local.Store.SaveOutgoingAsync(second.User.Id, "Kế hoạch nội bộ số " + i + " · Tệp và tin nhắn được mã hóa.");
        }
        await OnUi(async host =>
        {
            host.ClientSize = new Size(1920, 1080);
            var images = new ChatImageService(local.Host.Services.GetRequiredService<IFileTransferService>());
            using var view = ActivatorUtilities.CreateInstance<MainForm>(local.Host.Services, images);
            view.EmbedInWorkspace((_, _, _, _) => { }, () => true); view.Dock = DockStyle.Fill;
            host.Controls.Add(view); view.Show(); await view.StartViewAsync();
            Field<System.Windows.Forms.Timer>(view, "_timer").Stop();
            await view.SelectConversationAsync(a); await view.SelectConversationAsync(b);
            var samples = new List<double>();
            for (var i = 0; i < 12; i++)
            {
                var clock = Stopwatch.StartNew(); await view.SelectConversationAsync(i % 2 == 0 ? a : b);
                samples.Add(clock.Elapsed.TotalMilliseconds);
            }
            var rendering = new List<double>(); var synthetic = Enumerable.Range(0,80).Select(i => new LanChat.Domain.Message
            { Id=Guid.NewGuid(), ConversationId=a, SenderUserId=remote.User.Id, CreatedAtTicks=DateTime.UtcNow.Ticks+i, Text="Tin mẫu số "+i+" · Đo dựng danh sách cùng nội dung và số lượng.", IsRead=true }).ToArray();
            double coldRender; float bodyFont = 0;
            using (var list = new MessageListControl())
            {
                host.Controls.Add(list); list.BringToFront(); var clock=Stopwatch.StartNew();
                await list.PrepareAsync(80,default); list.Bind(a,synthetic,remote.User.Id,[]); coldRender=clock.Elapsed.TotalMilliseconds;
                bodyFont = Children(list).OfType<RichTextBox>().First().Font.SizeInPoints;
                for(var i=0;i<6;i++) { list.Bind(b,[],remote.User.Id,[]); clock.Restart(); list.Bind(a,synthetic,remote.User.Id,[]); rendering.Add(clock.Elapsed.TotalMilliseconds); }
            }
            await FrameworkIO.WriteTextAsync(Path.Combine(output, "performance.json"), JsonSerializer.Serialize(new
            { MessagesPerConversation = 24, WarmSwitches = samples, MedianMs = samples.OrderBy(x => x).Skip((samples.Count - 1) / 2).Take(samples.Count % 2 == 0 ? 2 : 1).Average(),
                ConversationBodyFontPoints = Children(view).OfType<RichTextBox>().First().Font.SizeInPoints,
                MessageRendering = new { Count=80, ColdPrepareAndBindMs=coldRender, WarmBindMs=rendering, BodyFontPoints=bodyFont },
                Runtime = Environment.Version.ToString(), Dpi = view.DeviceDpi }, new JsonSerializerOptions { WriteIndented = true }));
            await view.SelectConversationAsync(a);
            Field<ChatViewControl>(view, "_chat").RestoreDraft(new ComposerDraft("Bản nháp để đối chiếu khi đổi kích thước", [], null, null, ""));
            foreach (var size in new[] { new Size(660,450), new Size(800,600), new Size(1024,768), new Size(1280,800), new Size(1920,1080) })
            {
                host.ClientSize = size; await Task.Delay(80);
                using var bitmap = new Bitmap(view.Width, view.Height); view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, view.Size));
                bitmap.Save(Path.Combine(output, $"chat-{size.Width}.png"));
                Assert.Equal("Bản nháp để đối chiếu khi đổi kích thước", Field<ChatViewControl>(view, "_chat").Draft().Text);
            }
        });
    }
}
