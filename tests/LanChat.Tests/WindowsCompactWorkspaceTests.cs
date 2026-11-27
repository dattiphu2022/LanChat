using System.Drawing;
using System.Windows.Forms;
using LanChat.Application;
using LanChat.WinForms;
using LanChat.WinForms.Controls;
using LanChat.WinForms.Forms;
using Xunit;

namespace LanChat.Tests;

public sealed partial class WindowsUiTests
{
    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public Task CompactWorkspaceUsesTitleAndKeepsUpdateActionsAccessible(int dpi) => OnUi(async host =>
    {
        var allowDownload = Signal();
        using var fixture = new Updates(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Manifest();
            await Wait(allowDownload.Task, ct);
            return Payload();
        });
        using var workspace = new NetworkWorkspace(new NetworkCatalog(new NodeOptions { DataDirectory = Temp() }), new WindowsStartup(Temp()), fixture.Controller.Service);
        workspace.Show(host);
        using var fonts = SimulateDpi(workspace, dpi);
        workspace.ClientSize = new Size(660 * dpi / 96, 450 * dpi / 96);
        var install = Button(workspace, "Tải và Cài đặt");
        var cancel = Button(workspace, "Hủy tải");
        var more = (IconButton)Button(workspace, "Thêm");
        Assert.Same(more.Parent, install.Parent);
        Assert.Same(more.Parent, cancel.Parent);
        Assert.Same(install, more.Parent!.Controls.Cast<Control>().Last());
        Assert.False(install.Visible);
        Assert.False(cancel.Visible);
        var controller = (UpdateCoordinator)typeof(NetworkWorkspace).GetField("_updates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(workspace)!;
        controller.Notify("Bạn đang dùng phiên bản mới nhất.");
        Assert.Contains("Bạn đang dùng phiên bản mới nhất.", workspace.Text);
        Assert.Contains(NodeInfoDto.CurrentApplicationVersion, workspace.Text);
        Assert.False(install.Visible);
        var check = controller.CheckAsync(true);
        try
        {
            await Until(() => controller.State.Downloading);
            Assert.True(install.Visible);
            Assert.False(install.Enabled);
            Assert.True(cancel.Visible);
            Assert.True(cancel.Enabled);
            AssertToolbarFits();
        }
        finally { allowDownload.TrySetResult(true); }
        await check;
        Assert.True(controller.State.Ready);
        Assert.True(install.Visible);
        Assert.True(install.Enabled);
        Assert.False(cancel.Visible);
        Assert.True(more.ShowBadge);
        Assert.Null(workspace.PendingInstaller);
        AssertToolbarFits();
        void AssertToolbarFits()
        {
            foreach (var button in Children(workspace).OfType<Button>().Where(b => b.Visible))
            {
                var bounds = button.RectangleToScreen(button.ClientRectangle);
                var visible = bounds;
                for (var parent = button.Parent; parent is not null; parent = parent.Parent)
                    visible = Rectangle.Intersect(visible, parent.RectangleToScreen(parent.ClientRectangle));
                Assert.True(bounds == visible, $"Nút '{button.Text}' bị khuất ở {dpi} DPI: {bounds}, vùng hiển thị {visible}.");
            }
        }
        var captures = Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if (!string.IsNullOrEmpty(captures))
        {
            Directory.CreateDirectory(captures); using var bitmap = new Bitmap(workspace.Width, workspace.Height);
            workspace.DrawToBitmap(bitmap, new Rectangle(Point.Empty, workspace.Size)); bitmap.Save(Path.Combine(captures, $"compact-workspace-{dpi}.png"));
        }
    });
}
