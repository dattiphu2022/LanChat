using System.Windows.Forms;
using LanChat.WinForms.Controls;
using LanChat.WinForms.Forms;
using Xunit;
using ChatMessage = LanChat.Domain.Message;

namespace LanChat.Tests;

public sealed partial class WindowsUiTests
{
    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public Task MultiSelectionKeepsControlsDraftAndQrActionsAccessible(int dpi) => OnUi(async host =>
    {
        using var canvas = new DpiForm { ClientSize = new System.Drawing.Size(440, 560), ShowInTaskbar = false }; canvas.Show(host);
        using var chat = new ChatViewControl(); canvas.Controls.Add(chat); chat.SelectConversation("A", true);
        var conversation = Guid.NewGuid(); var author = Guid.NewGuid();
        var messages = Enumerable.Range(0, 8).Select(i => new ChatMessage { Id = Guid.NewGuid(), ConversationId = conversation, SenderUserId = author,
            Text = "Tin " + i + " — nội dung thử nghiệm", CreatedAtTicks = DateTime.UtcNow.Ticks + i, IsRead = true }).ToArray();
        chat.Bind(conversation, messages, author, [], false); chat.RestoreDraft(new("Bản nháp phải giữ", [], null, null, ""));
        using var fonts = SimulateDpi(canvas, dpi); canvas.ClientSize = new System.Drawing.Size(440 * dpi / 96, 560 * dpi / 96);
        await Task.Delay(60);
        foreach (var control in Children(canvas).Prepend(canvas))
            typeof(Control).GetField("deviceDpi", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(control, dpi);
        foreach (var control in Children(chat).Where(c => c is MessageBubbleControl or ChatInputControl).Prepend(chat))
            typeof(Control).GetMethod("OnDpiChangedAfterParent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(control, [EventArgs.Empty]);
        await Task.Delay(60);
        var controls = Children(chat).OfType<MessageBubbleControl>().ToArray();
        var select = Children(chat).OfType<Button>().Single(b => b.AccessibleName == "Chọn nhiều"); select.PerformClick(); await Task.Delay(80);
        var boxes = controls.Select(b => Children(b).OfType<CheckBox>().Single()).ToArray(); Assert.All(boxes, b => Assert.True(b.Visible));
        Assert.False(Button(chat, "Xuất QR").Enabled); boxes[2].Checked = true; Assert.True(Button(chat, "Xuất QR").Enabled);
        Assert.Equal("Bản nháp phải giữ", chat.Draft().Text);
        Button(chat, "Chọn tất cả").PerformClick(); Assert.All(boxes, b => Assert.True(b.Checked));
        Assert.Equal(controls, Children(chat).OfType<MessageBubbleControl>().ToArray());
        foreach (var button in new[] { Button(chat, "Chọn tất cả"), Button(chat, "Bỏ chọn"), Button(chat, "Xuất QR"), Button(chat, "Hủy chọn"), Button(chat, "Gửi") })
        {
            var bounds = button.RectangleToScreen(button.ClientRectangle); var visible = bounds;
            for (var parent = button.Parent; parent is not null; parent = parent.Parent) visible = System.Drawing.Rectangle.Intersect(visible, parent.RectangleToScreen(parent.ClientRectangle));
            Assert.Equal(bounds, visible);
        }
        var captures = Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if (!string.IsNullOrEmpty(captures)) { Directory.CreateDirectory(captures); using var image = new System.Drawing.Bitmap(chat.Width, chat.Height); chat.DrawToBitmap(image, chat.ClientRectangle); image.Save(Path.Combine(captures, $"message-selection-{dpi}.png")); }
        Button(chat, "Bỏ chọn").PerformClick(); Assert.All(boxes, b => Assert.False(b.Checked));
        boxes[0].Checked = true;
        var driver = Signal();
        host.BeginInvoke((Action)(async () =>
        { try { await Until(() => System.Windows.Forms.Application.OpenForms.OfType<QrShareForm>().Any()); var form = System.Windows.Forms.Application.OpenForms.OfType<QrShareForm>().Single(); Assert.True(form.CanExport); Assert.Equal(messages[0].Text, Children(form).OfType<TextBox>().Single().Text); Button(form, "Đóng").PerformClick(); driver.TrySetResult(true); } catch (Exception ex) { driver.TrySetException(ex); } }));
        Button(chat, "Xuất QR").PerformClick(); await driver.Task;
        messages[0].Text = new string('x', 501); chat.Bind(conversation, messages, author, [], false);
        Assert.False(Button(chat, "Xuất QR").Enabled); Assert.True(boxes[0].Checked);
        Button(chat, "Hủy chọn").PerformClick(); Assert.All(boxes, b => Assert.False(b.Visible));
        Assert.Equal("Bản nháp phải giữ", chat.Draft().Text);
    });
}
