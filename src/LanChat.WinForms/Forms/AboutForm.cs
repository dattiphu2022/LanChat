using LanChat.Application;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed class AboutForm : DpiForm
{
    public AboutForm(string updateMessage, Func<Task<string>> checkForUpdate)
    {
        Text = "Giới thiệu LanChat";
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(524, 420);

        var layout = Theme.Stack(5);
        layout.Padding = new Padding(24, 20, 24, 16);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 91));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

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
        author.BackColor = Theme.Canvas;
        author.Margin = new Padding(0, 0, 0, 6);
        author.Padding = new Padding(12, 8, 12, 8);
        author.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        author.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var credit = Theme.Label("Tác giả", 9);
        credit.ForeColor = Theme.Muted;
        author.Controls.Add(credit, 0, 0);
        author.Controls.Add(Theme.Label("Trung tá Nguyễn Quốc Đạt", 15, true), 0, 1);

        var introduction = Theme.Label("LanChat phục vụ trao đổi công việc và chia sẻ tài liệu giữa các máy trong mạng nội bộ (LAN).\n\n" +
            "Ứng dụng hỗ trợ chat riêng, chat nhóm và gửi ảnh, tệp, thư mục. Tin nhắn và tệp được mã hóa đầu cuối. " +
            "Tin đang chờ sẽ tự đồng bộ khi kết nối trở lại.");
        introduction.AutoSize = false;
        introduction.Margin = new Padding(0, 12, 0, 0);

        var footer = Theme.Toolbar();
        footer.FlowDirection = FlowDirection.RightToLeft;
        footer.Margin = Padding.Empty;
        var close = Theme.Button("Đóng");
        close.DialogResult = DialogResult.OK;
        footer.Controls.Add(close);
        var check = Theme.Secondary("Kiểm tra cập nhật");
        footer.Controls.Add(check);
        var status = Theme.Label(updateMessage, 9);
        status.ForeColor = Theme.Muted;
        status.UseMnemonic = false;
        check.Click += async (_, _) =>
        {
            check.Enabled = false;
            status.Text = "Đang kiểm tra cập nhật…";
            var message = await checkForUpdate();
            if (IsDisposed) return;
            status.Text = message;
            check.Enabled = true;
        };
        AcceptButton = CancelButton = close;

        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(author, 0, 1);
        layout.Controls.Add(introduction, 0, 2);
        layout.Controls.Add(status, 0, 3);
        layout.Controls.Add(footer, 0, 4);
        Controls.Add(layout);
    }
}
