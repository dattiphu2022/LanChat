using System.Drawing;
using System.Windows.Forms;
using LanChat.Application;
using LanChat.Domain;
using LanChat.WinForms.Controls;
using LanChat.WinForms.Forms;
using Xunit;

namespace LanChat.Tests;

public sealed partial class WindowsUiTests
{
    [Fact]
    public Task ForwardResultsRetryOnlyUnsentItems() => OnUi(async host =>
    {
        var service = new ForwardFixture();
        var requests = new[] { new ForwardRequest(Guid.NewGuid(), new(Guid.NewGuid(), false, "A")), new ForwardRequest(Guid.NewGuid(), new(Guid.NewGuid(), false, "B")) };
        using var results = new ForwardBatchForm(service, requests, CancellationToken.None); results.Show(host);
        await Until(() => Button(results, "Thử lại mục chưa gửi").Enabled);
        Assert.Equal(2, service.Calls[0].Length); Button(results, "Thử lại mục chưa gửi").PerformClick();
        await Until(() => service.Calls.Count == 2 && Button(results, "Đóng").Enabled);
        Assert.Equal(requests[1], Assert.Single(service.Calls[1]));
        Assert.False(Button(results, "Thử lại mục chưa gửi").Enabled);
    });

    private sealed class ForwardFixture : IChatService
    {
        internal List<ForwardRequest[]> Calls { get; } = [];
        public Task<LanChat.Domain.Message> SendAsync(Guid recipientId, string text, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LanChat.Domain.Message> ForwardAsync(Guid messageId, Guid conversationId, IProgress<FileProgress>? progress = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ForwardResult[]> ForwardBatchAsync(ForwardRequest[] requests, IProgress<ForwardProgress>? progress = null, CancellationToken ct = default)
        {
            Calls.Add(requests);
            return Task.FromResult(requests.Select((request, i) => new ForwardResult(request,
                Calls.Count == 1 && i == 1 ? ForwardOutcome.Failed : ForwardOutcome.Queued, Error: "Lỗi giả lập")).ToArray());
        }
    }

    [Fact]
    public Task MentionSuggestionsConsumeEnterEscapeAndKeepMetadataInDraftAndEdit() => OnUi(async host =>
    {
        using var composer = new ChatInputControl(); host.Controls.Add(composer);
        var user = Guid.NewGuid(); var local = Guid.NewGuid();
        composer.ConfigureMentions([new(Guid.Empty, "All", null, "", true), new(user, "Đặng Thị Hà", "Kế toán", "PC-Ha")], [user, local]);
        var editor = Children(composer).OfType<PromptTextBox>().Single(); editor.Focus(); Assert.True(editor.Focused); editor.SelectedText = "@dang";
        await Task.Delay(40);
        Assert.True(editor.InterceptKey!(Keys.Enter));
        var draft = composer.Draft(); Assert.Equal("@Đặng Thị Hà ", draft.Text); Assert.Equal(user, Assert.Single(Assert.Single(draft.Mentions).UserIds));
        editor.Undo(); Assert.Empty(composer.Draft().Mentions);
        editor.Undo(); Assert.Equal(user, Assert.Single(Assert.Single(composer.Draft().Mentions).UserIds));
        composer.RestoreDraft(null); editor.Focus(); editor.SelectedText = "@"; await Task.Delay(20);
        Assert.True(editor.InterceptKey(Keys.Escape)); Assert.False(editor.InterceptKey(Keys.Enter));
        composer.RestoreDraft(draft); composer.SetContext(new() { Id = Guid.NewGuid(), Text = "@All sửa" }, true, [new(0, 4, true, [user, local])]);
        Assert.True(Assert.Single(composer.Draft().Mentions).All);
        composer.ResetContext(); Assert.Equal(draft.Text, composer.Draft().Text); Assert.Equal(user, Assert.Single(Assert.Single(composer.Draft().Mentions).UserIds));
        composer.SendRequested += _ => throw new IOException("Lỗi gửi thử nghiệm");
        Button(composer, "Gửi").PerformClick(); await Task.Delay(30);
        Assert.Equal(draft.Text, composer.Draft().Text); Assert.Single(composer.Draft().Mentions);
    });

    [Fact]
    public Task MentionRenderingMapsCrLfOffsetsAndRetainsPlainText() => OnUi(async host =>
    {
        var user = Guid.NewGuid(); const string raw = "👋\r\n@Hà và @All";
        using var body = new MentionTextControl(); host.Controls.Add(body);
        body.BindText(raw, [new(4, 3, false, [user]), new(11, 4, true, [Guid.NewGuid()])], user, "", Color.White, Color.Black, false);
        Assert.Equal("👋\n@Hà và @All", body.Text);
        body.Select(3, 3); Assert.Equal(Theme.Blue, body.SelectionColor);
        body.Select(10, 4); Assert.Equal(Color.Black.ToArgb(), body.SelectionColor.ToArgb());
        body.Select(4, 1); body.BindText(raw, [new(4, 3, false, [user])], user, "", Color.White, Color.Black, false);
        Assert.Equal(4, body.SelectionStart); Assert.Equal(1, body.SelectionLength);
        await Task.CompletedTask;
    });

    [Theory]
    [InlineData(96)] [InlineData(144)] [InlineData(192)]
    public Task ForwardPickerShowsAllContactsKeepsChecksAndButtonsAtHighDpi(int dpi) => OnUi(async host =>
    {
        var users = Enumerable.Range(0, 20).Select(i => new UserSummary(Guid.NewGuid(), i < 2 ? "Tên trùng" : "Người " + i,
            i == 0 ? "Kế toán" : "Kỹ thuật", "PC-" + i, "", 0, Guid.NewGuid(), 0, PresenceStatus.Offline)).ToArray();
        using var picker = new ForwardRecipientsForm(users, []); picker.Show(host);
        using var fonts = SimulateDpi(picker, dpi); picker.ClientSize = new Size(420 * dpi / 96, 400 * dpi / 96);
        var list = Children(picker).OfType<CheckedListBox>().First(c => c.Visible);
        var search = Children(picker).OfType<PromptTextBox>().Single();
        Assert.Equal(20, list.Items.Count); Assert.False(Button(picker, "Chuyển tiếp").Enabled);
        list.SetItemChecked(0, true); var selected = Assert.Single(picker.Selected);
        search.Text = "ke toan"; Assert.Single(list.Items); search.Text = "";
        Assert.Equal(selected.Id, Assert.Single(picker.Selected).Id); Assert.Single(list.CheckedItems);
        list.SetItemChecked(1, true); Assert.Equal(2, picker.Selected.Length); Assert.True(Button(picker, "Chuyển tiếp").Enabled);
        await Task.Delay(40);
        foreach (var button in Children(picker).OfType<Button>().Where(b => b.Visible))
        {
            var bounds = button.RectangleToScreen(button.ClientRectangle); var visible = bounds;
            for (var parent = button.Parent; parent is not null; parent = parent.Parent) visible = Rectangle.Intersect(visible, parent.RectangleToScreen(parent.ClientRectangle));
            Assert.Equal(bounds, visible);
        }
        var captures = Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if (!string.IsNullOrEmpty(captures)) { Directory.CreateDirectory(captures); using var image = new Bitmap(picker.Width, picker.Height); picker.DrawToBitmap(image, picker.ClientRectangle with { Width = picker.Width, Height = picker.Height }); image.Save(Path.Combine(captures, $"forward-picker-{dpi}.png")); }
    });
}
