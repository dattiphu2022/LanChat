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
        using var fixture = new Updates((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com" ? Manifest() : Payload()));
        using var workspace = new NetworkWorkspace(new NetworkCatalog(new NodeOptions { DataDirectory = Temp() }), new WindowsStartup(Temp()), fixture.Controller.Service);
        workspace.Show(host);
        using var fonts = SimulateDpi(workspace, dpi);
        workspace.ClientSize = new Size(660 * dpi / 96, 450 * dpi / 96);
        var panel = Children(workspace).OfType<UpdatePanel>().Single();
        Assert.False(panel.Visible);
        var controller = (UpdateCoordinator)typeof(NetworkWorkspace).GetField("_updates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(workspace)!;
        controller.Notify("Bạn đang dùng phiên bản mới nhất.");
        Assert.Contains("Bạn đang dùng phiên bản mới nhất.", workspace.Text);
        Assert.Contains(NodeInfoDto.CurrentApplicationVersion, workspace.Text);
        Assert.False(panel.Visible);
        await controller.CheckAsync(true); await Task.Delay(40);
        Assert.True(panel.Visible);
        var install = Button(panel, "Cài đặt ngay"); Assert.True(install.Visible); Assert.True(install.Enabled);
        Assert.True(((IconButton)Button(workspace, "Thêm")).ShowBadge);
        Assert.Null(workspace.PendingInstaller);
        Assert.DoesNotContain(Children(panel).OfType<Label>(), label => label.Visible);
        foreach (var button in Children(workspace).OfType<Button>().Where(b => b.Visible))
        {
            var bounds = button.RectangleToScreen(button.ClientRectangle); var visible = bounds;
            for (var parent = button.Parent; parent is not null; parent = parent.Parent) visible = Rectangle.Intersect(visible, parent.RectangleToScreen(parent.ClientRectangle));
            Assert.Equal(bounds, visible);
        }
        var captures = Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if (!string.IsNullOrEmpty(captures))
        {
            Directory.CreateDirectory(captures); using var bitmap = new Bitmap(workspace.Width, workspace.Height);
            workspace.DrawToBitmap(bitmap, new Rectangle(Point.Empty, workspace.Size)); bitmap.Save(Path.Combine(captures, $"compact-workspace-{dpi}.png"));
        }
    });
}
