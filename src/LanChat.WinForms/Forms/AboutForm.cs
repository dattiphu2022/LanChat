using System.ComponentModel;
using System.Diagnostics;
using LanChat.Application;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed class AboutForm : DpiForm
{
    internal const string AuthorName = "Trung tá Nguyễn Quốc Đạt - K02 - BCA";
    private const string ProjectUrl = "https://github.com/dattiphu2022/LanChat.git";

    internal bool InstallRequested { get; private set; }

    public AboutForm(UpdateCoordinator updates, Func<bool> canInstall, Action? configureUpdates = null)
    {
        Text = "Giới thiệu LanChat";
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(560, 560);
        MinimumSize = new Size(500, 450);
        AutoScroll = true;

        var layout = Theme.Stack(4);
        layout.Padding = new Padding(24, 20, 24, 16);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        var content = Theme.Stack(4); content.Dock = DockStyle.Top; content.AutoSize = true; content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 91));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 3 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        using var largeIcon = new Icon(AppIcon.Value, new Size(48, 48));
        var icon = largeIcon.ToBitmap();
        Disposed += (_, _) => icon.Dispose();
        var logo = new PictureBox { Image = icon, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(48, 48),
            Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) };
        header.Controls.Add(logo, 0, 0);
        header.SetRowSpan(logo, 3);
        header.Controls.Add(Theme.Label("LanChat", 24, true), 1, 0);
        header.Controls.Add(Theme.Label("Nhắn tin nội bộ trên Windows"), 1, 1);
        var version = Theme.Label($"Phiên bản {NodeInfoDto.CurrentApplicationVersion}  ·  Giao thức v{NodeInfoDto.CurrentProtocol}", 9);
        version.ForeColor = Theme.Muted;
        header.Controls.Add(version, 1, 2);

        var author = Theme.Stack(2);
        author.Dock = DockStyle.Top; author.AutoSize = true; author.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        author.BackColor = Theme.Canvas;
        author.Margin = new Padding(0, 0, 0, 6);
        author.Padding = new Padding(12, 8, 12, 8);
        author.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        author.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var credit = Theme.Label("Tác giả", 9);
        credit.ForeColor = Theme.Muted;
        author.Controls.Add(credit, 0, 0);
        var authorName = Theme.Label(AuthorName, 15, true); UiInteraction.Wrap(authorName, author);
        author.Controls.Add(authorName, 0, 1);

        var introduction = Theme.Label("LanChat phục vụ trao đổi công việc và chia sẻ tài liệu giữa các máy trong mạng nội bộ (LAN).\n\n" +
            "Ứng dụng hỗ trợ chat riêng, chat nhóm và gửi ảnh, tệp, thư mục. Tin nhắn và tệp được mã hóa đầu cuối. " +
            "Tin đang chờ sẽ tự đồng bộ khi kết nối trở lại.");
        UiInteraction.Wrap(introduction, content);
        introduction.Margin = new Padding(0, 12, 0, 0);

        var resources = Theme.Toolbar();
        resources.Margin = Padding.Empty;
        LinkLabel ResourceLink(string text, UiIcon icon) => new IconLinkLabel(icon) { Text = text,
            Margin = new Padding(5, 4, 12, 4), LinkColor = Theme.Blue };
        var guide = ResourceLink("Hướng dẫn sử dụng", UiIcon.Book);
        guide.LinkClicked += (_, _) =>
        {
            using var dialog = new UserGuideForm();
            UiInteraction.ShowDialog(dialog, this);
        };
        var project = ResourceLink("Dự án trên GitHub", UiIcon.Open);
        project.AccessibleDescription = ProjectUrl;
        var license = ResourceLink("Giấy phép MIT", UiIcon.File);
        license.LinkClicked += (_, _) =>
        {
            using var dialog = new LicenseForm();
            UiInteraction.ShowDialog(dialog, this);
        };
        var thirdParty = ResourceLink("Giấy phép thư viện", UiIcon.File);
        thirdParty.LinkClicked += (_, _) =>
        {
            using var dialog = new LicenseForm(showThirdParty: true);
            UiInteraction.ShowDialog(dialog, this);
        };
        resources.Controls.AddRange([guide, project, license, thirdParty]);
        var links = new ToolTip();
        links.SetToolTip(project, ProjectUrl);
        Disposed += (_, _) => links.Dispose();

        var footer = Theme.Toolbar();
        footer.FlowDirection = FlowDirection.LeftToRight;
        footer.Margin = Padding.Empty;
        var close = Theme.Button("Đóng", UiIcon.Close);
        close.DialogResult = DialogResult.OK;
        var donation = Theme.Secondary("Ủng hộ phát triển", UiIcon.Heart);
        donation.Click += (_, _) =>
        {
            using var dialog = new DonationForm();
            UiInteraction.ShowDialog(dialog, this);
        };
        footer.Controls.Add(donation);
        footer.Controls.Add(close);
        var status = Theme.Label("", 9);
        status.ForeColor = Theme.Muted;
        status.UseMnemonic = false;
        status.Visible = false;
        project.LinkClicked += (_, _) =>
        {
            try
            {
                using var browser = Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                status.Text = "Chưa mở được trình duyệt. " + ProjectUrl;
                status.Visible = true;
            }
        };
        var updatePanel = new UpdatePanel(updates, configure: configureUpdates);
        (int Width, int Dpi, int FontHeight, string Text)? actionLayout = null;
        void AlignActions()
        {
            var buttons = updatePanel.ActionButtons.Concat(new[] { donation, close }).ToArray();
            var key = (layout.ClientSize.Width, this.LayoutDpi(), buttons.Max(button => button.Font.Height), string.Join("\0", buttons.Select(button => button.Text)));
            if (actionLayout == key) return;
            actionLayout = key;
            foreach (var button in buttons) button.MinimumSize = Size.Empty;
            var preferred = buttons.Select(button => button.GetPreferredSize(Size.Empty)).ToArray();
            var width = Math.Min(preferred.Max(size => size.Width), Math.Max(1, (layout.ClientSize.Width - layout.Padding.Horizontal) / 2 - this.ScalePixels(10)));
            var height = Math.Max(this.ScalePixels(36), preferred.Max(size => size.Height));
            foreach (var button in buttons)
            {
                button.AutoSize = false;
                button.Size = button.MinimumSize = new Size(width, height);
                button.Margin = new Padding(this.ScalePixels(5));
            }
        }
        layout.SizeChanged += (_, _) => AlignActions();
        DpiChanged += (_, _) => AlignActions();
        updates.Changed += AlignActions;
        Disposed += (_, _) => updates.Changed -= AlignActions;
        updatePanel.InstallRequested += () =>
        {
            if (!canInstall())
            {
                updates.Notify("Hãy hoàn tất hoặc hủy tác vụ đang chạy rồi chọn Cài đặt ngay.");
                return;
            }
            InstallRequested = true;
            DialogResult = DialogResult.OK;
        };
        AcceptButton = CancelButton = close;
        Shown += (_, _) => { close.Focus(); scroll.AutoScrollPosition = Point.Empty; };

        content.Controls.Add(header, 0, 0); content.Controls.Add(author, 0, 1);
        content.Controls.Add(introduction, 0, 2); content.Controls.Add(resources, 0, 3); scroll.Controls.Add(content);
        UiInteraction.Wrap(status, layout);
        layout.Controls.Add(scroll, 0, 0); layout.Controls.Add(updatePanel, 0, 1);
        layout.Controls.Add(status, 0, 2); layout.Controls.Add(footer, 0, 3);
        Controls.Add(layout);
        AlignActions();
    }
}
