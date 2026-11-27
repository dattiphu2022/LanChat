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
    [Fact]
    public async Task DisplayPreferencesRoundTripInIsolatedLocalConfiguration()
    {
        var directory=Temp(); var catalog=new NetworkCatalog(new NodeOptions {DataDirectory=directory});
        var preferences=new UiPreferences(14,UiDensity.Comfortable,true,true,UiTheme.Dark, false, 12, SaveSystemResources: true);
        await catalog.SaveUiPreferencesAsync(preferences);
        var reloaded=new NetworkCatalog(new NodeOptions {DataDirectory=directory}); await reloaded.LoadAsync();
        Assert.Equal(preferences,reloaded.UiPreferences);
        Assert.True(File.Exists(Path.Combine(directory,"ui-preferences.json")));
    }

    [Fact]
    public void ExistingPreferencesDefaultToLight()
    {
        var preferences = System.Text.Json.JsonSerializer.Deserialize<UiPreferences>(
            "{\"MessageFontSize\":12,\"HideNotificationContent\":true}")!;
        Assert.Equal(UiTheme.Light, preferences.Validated().Theme);
        Assert.Equal(12, preferences.MessageFontSize);
        Assert.True(preferences.HideNotificationContent);
        Assert.True(preferences.RemindUnreadMessages);
        Assert.False(preferences.SaveSystemResources);
        Assert.Equal(5, preferences.UnreadReminderMinutes);
        Assert.Equal(5, (preferences with { UnreadReminderMinutes = 0 }).Validated().UnreadReminderMinutes);
    }

    [Theory]
    [InlineData(false, 4, 1, 4)]
    [InlineData(true, 4, 0, 0)]
    [InlineData(true, 4, 1, 1)]
    [InlineData(true, 0, 1, 0)]
    public void UnreadReminderRespectsMuteAndKeepsUnreadMentions(bool muted, int unread, int mentions, int expected)
    {
        var summary = new LanChat.Application.ConversationSummary(Guid.NewGuid(), Guid.NewGuid(),
            "Hội thoại", "", 0, unread, false, muted, LanChat.Domain.PresenceStatus.Unknown)
        { UnreadMentionCount = mentions };
        Assert.Equal(expected, MainForm.ReminderCount(summary));
    }

    [Theory]
    [InlineData(660, 96)] [InlineData(800, 120)] [InlineData(1280, 144)] [InlineData(1280, 192)]
    public Task OpenChatChangesThemeAndKeepsSelection(int width, int dpi) => OnUi(async host =>
    {
        using var form = new DpiForm { ClientSize = new Size(width, 520), Text = "LanChat · Kiểm tra giao diện" };
        using var chat = new ChatViewControl();
        form.Controls.Add(chat);
        form.Show(host);
        using var fonts = SimulateDpi(form, dpi);
        var local = Guid.NewGuid();
        chat.SelectConversation("Đồng nghiệp", true);
        var conversation = Guid.NewGuid();
        var peer = Guid.NewGuid();
        var messages = new[]
        {
            new LanChat.Domain.Message { Id = Guid.NewGuid(), ConversationId = conversation, SenderUserId = peer,
                CreatedAtTicks = DateTime.UtcNow.AddMinutes(-2).Ticks, Text = "Bạn đã xem bản tổng hợp chưa?", IsRead = true },
            new LanChat.Domain.Message { Id = Guid.NewGuid(), ConversationId = conversation, SenderUserId = local,
                CreatedAtTicks = DateTime.UtcNow.AddMinutes(-1).Ticks, Text = "Mình đang xem. Chiều nay mình gửi phản hồi.", IsRead = true }
        };
        chat.Bind(conversation, messages, local, [], false);
        chat.RestoreDraft(new("Bản nháp giữ lại", [], null, null, ""));
        using var mention = new MentionTextControl { Dock = DockStyle.Bottom, Height = 28 };
        form.Controls.Add(mention);
        mention.BringToFront();
        mention.BindText("Chào @Hà", [new(5, 3, false, [local])], local, "", Theme.Surface, Theme.Ink, false);
        mention.Select(5, 3);
        var output = Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        void Capture(string mode)
        {
            if (string.IsNullOrEmpty(output)) return;
            Directory.CreateDirectory(output);
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(output, $"chat-{mode}-{width}-{dpi}.png"));
        }

        try
        {
            await Task.Delay(20);
            Capture("light");
            Theme.SetMode(UiTheme.Dark);
            if (SystemInformation.HighContrast) return;
            Assert.True(Theme.IsDark);
            Assert.Equal(Theme.Surface, mention.BackColor);
            Assert.Equal(Theme.Blue.ToArgb(), mention.SelectionColor.ToArgb());
            Assert.Equal(5, mention.SelectionStart);
            Assert.Equal(3, mention.SelectionLength);
            Assert.Equal("Bản nháp giữ lại", chat.Draft().Text);
            await Task.Delay(20);
            Capture("dark");
        }
        finally { Theme.SetMode(UiTheme.Light); }
        Assert.Equal(Color.White, mention.BackColor);
        Assert.Equal(Theme.Blue.ToArgb(), mention.SelectionColor.ToArgb());
    });

    [Fact]
    public Task ChangingAppearanceKeepsDraftRowsAndReadingAnchor() => OnUi(async host =>
    {
        using var chat=new ChatViewControl(); host.Controls.Add(chat); chat.SelectConversation("Đồng nghiệp",true);
        var conversation=Guid.NewGuid(); var own=Guid.NewGuid();
        var messages=Enumerable.Range(0,40).Select(i=>new LanChat.Domain.Message {Id=Guid.NewGuid(),ConversationId=conversation,SenderUserId=own,
            CreatedAtTicks=DateTime.UtcNow.Ticks+i,Text="Tin thử nghiệm số "+i+" · Thay đổi cỡ chữ không làm mất vị trí đang đọc.",IsRead=true}).ToArray();
        chat.Bind(conversation,messages,own,[],false); chat.RestoreDraft(new("Bản nháp giữ lại",[],null,null,"")); await Task.Delay(40);
        var list=Children(chat).OfType<MessageListControl>().Single(); var flow=list.Controls.OfType<FlowLayoutPanel>().Single();
        flow.AutoScrollPosition=new Point(0,420); await Task.Delay(30);
        var rows=Children(list).OfType<MessageBubbleControl>().ToArray(); var anchor=rows.First(r=>r.Bottom>0); var top=anchor.Top;
        chat.ApplyPreferences(new(14,UiDensity.Comfortable)); await Task.Delay(40);
        Assert.Equal("Bản nháp giữ lại",chat.Draft().Text); Assert.Equal(rows,Children(list).OfType<MessageBubbleControl>().ToArray());
        Assert.InRange(Math.Abs(anchor.Top-top),0,3); Assert.False(list.AtBottom);
        Assert.All(Children(list).OfType<MentionTextControl>(),c=>Assert.InRange(c.Font.SizeInPoints,13.9f,14.1f));
    });

    [Theory]
    [InlineData(96)] [InlineData(120)] [InlineData(144)] [InlineData(192)]
    public Task PreferencesAndGettingStartedDialogsFitDpiAndSaveWithoutRestart(int dpi) => OnUi(async host =>
    {
        UiPreferences? saved=null;
        using var settings=new SettingsForm("Người thử nghiệm","Đơn vị",false,(_,_,_)=>Task.CompletedTask,
            preferences:new UiPreferences(),savePreferences:p=>{saved=p;return Task.CompletedTask;}); settings.Show(host);
        using var fonts=SimulateDpi(settings,dpi); settings.ClientSize=new Size(480*dpi/96,470*dpi/96);
        Children(settings).OfType<ComboBox>().Single(c=>c.AccessibleName=="Cỡ chữ tin nhắn").SelectedIndex=3;
        Children(settings).OfType<ComboBox>().Single(c=>c.AccessibleName=="Mật độ hiển thị").SelectedIndex=2;
        Children(settings).OfType<CheckBox>().Single(c=>c.AccessibleName=="Ẩn nội dung thông báo").Checked=true;
        await Task.Delay(30); var output=Environment.GetEnvironmentVariable("LANCHAT_UI_CAPTURE_DIR");
        if(!string.IsNullOrEmpty(output)) { Directory.CreateDirectory(output); using var bitmap=new Bitmap(settings.Width,settings.Height); settings.DrawToBitmap(bitmap,new Rectangle(Point.Empty,settings.Size)); bitmap.Save(Path.Combine(output,$"preferences-{dpi}.png")); }
        var reminder = Children(settings).OfType<CheckBox>().Single(c => c.Text == "Nhắc lại khi còn tin chưa đọc");
        var minutes = Children(settings).OfType<NumericUpDown>().Single();
        Assert.True(reminder.Checked);
        Assert.Equal(5, minutes.Value);
        minutes.Value = 12;
        reminder.Checked = false;
        var saveResources = Children(settings).OfType<CheckBox>().Single(c => c.Text == "Tiết kiệm tài nguyên hệ thống");
        Assert.False(saveResources.Checked);
        saveResources.Checked = true;
        Assert.False(minutes.Enabled);
        Button(settings,"Lưu").PerformClick(); await Until(()=>saved is not null); Assert.Equal(14,saved!.MessageFontSize); Assert.True(saved.HideNotificationContent);
        Assert.False(saved.RemindUnreadMessages);
        Assert.True(saved.SaveSystemResources);
        Assert.Equal(12, saved.UnreadReminderMinutes);
        using var guide=new GettingStartedForm(()=>"Người thử nghiệm",()=>false,()=>Task.CompletedTask,()=>Task.CompletedTask,()=>Task.CompletedTask);
        guide.Show(host); using var guideFonts=SimulateDpi(guide,dpi); await Task.Delay(30);
        Assert.False(Button(guide,"3. Kiểm tra kết nối").Enabled); Assert.True(Button(guide,"Để sau").CanSelect);
        if(!string.IsNullOrEmpty(output)) { using var bitmap=new Bitmap(guide.Width,guide.Height); guide.DrawToBitmap(bitmap,new Rectangle(Point.Empty,guide.Size)); bitmap.Save(Path.Combine(output,$"getting-started-{dpi}.png")); }
    });
}
