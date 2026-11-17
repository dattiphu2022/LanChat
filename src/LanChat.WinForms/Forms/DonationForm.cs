using System.Runtime.InteropServices;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed class DonationForm : DpiForm
{
    private const string BankName = "MBV - Ngân hàng TNHH MTV Việt Nam Hiện đại";
    private const string RecipientName = "NGUYỄN QUỐC ĐẠT";
    private const string AccountNumber = "98668899";

    public DonationForm()
    {
        Text = "Ủng hộ phát triển LanChat";
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(620, 540);

        var layout = Theme.Stack(3);
        layout.Padding = new Padding(24, 20, 24, 12);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var notice = Theme.Label("Nếu LanChat hữu ích, bạn có thể ủng hộ tác giả để duy trì và cải thiện dự án. " +
            "LanChat đã, đang và sẽ miễn phí khi sử dụng. Việc ủng hộ hoàn toàn tự nguyện và không mang lại thêm tính năng hay quyền lợi riêng.");
        notice.Margin = new Padding(0, 0, 0, 14);
        layout.Controls.Add(notice, 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var details = Theme.Stack(8);
        details.Dock = DockStyle.Top;
        details.AutoSize = true;
        details.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        details.BackColor = Theme.Canvas;
        details.Padding = new Padding(12);
        details.Margin = Padding.Empty;
        void AddCaption(string text, int row)
        {
            var caption = Theme.Label(text, 9);
            caption.ForeColor = Theme.Muted;
            caption.Margin = new Padding(0, row == 0 ? 0 : 12, 0, 2);
            details.Controls.Add(caption, 0, row);
        }

        AddCaption("Ngân hàng", 0);
        details.Controls.Add(Theme.Label(BankName, 10, true), 0, 1);
        AddCaption("Người nhận", 2);
        details.Controls.Add(Theme.Label(RecipientName, 10, true), 0, 3);
        AddCaption("Số tài khoản", 4);
        var account = new TextBox { Text = AccountNumber, AccessibleName = "Số tài khoản",
            ReadOnly = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        details.Controls.Add(account, 0, 5);

        var actions = Theme.Toolbar();
        actions.Margin = Padding.Empty;
        var copy = Theme.Secondary("Sao chép số tài khoản");
        var save = Theme.Secondary("Lưu ảnh QR");
        actions.Controls.AddRange([copy, save]);
        details.Controls.Add(actions, 0, 6);
        var status = Theme.Label("", 9);
        status.ForeColor = Theme.Muted;
        status.AccessibleRole = AccessibleRole.StatusBar;
        details.Controls.Add(status, 0, 7);
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(AccountNumber);
                status.Text = "Đã sao chép số tài khoản.";
            }
            catch (ExternalException)
            {
                account.Focus();
                account.SelectAll();
                status.Text = "Số tài khoản đã được chọn. Nhấn Ctrl+C để sao chép.";
            }
        };
        save.Click += (_, _) => SaveQr(status);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0, 0, 12, 0) };
        scroll.Controls.Add(details);
        body.Controls.Add(scroll, 0, 0);

        using var stream = OpenQr();
        using var original = Image.FromStream(stream);
        var qr = new Bitmap(original);
        Disposed += (_, _) => qr.Dispose();
        var qrPanel = Theme.Stack(2);
        qrPanel.Margin = Padding.Empty;
        qrPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        qrPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var preview = QrPicture(qr);
        preview.Cursor = Cursors.Hand;
        preview.Click += (_, _) => ShowQr(qr);
        var enlarge = Theme.Secondary("Phóng lớn mã QR");
        enlarge.Anchor = AnchorStyles.None;
        enlarge.Click += (_, _) => ShowQr(qr);
        qrPanel.Controls.Add(preview, 0, 0);
        qrPanel.Controls.Add(enlarge, 0, 1);
        body.Controls.Add(qrPanel, 1, 0);
        layout.Controls.Add(body, 0, 1);
        AddCloseButton(this, layout, 2);
        Controls.Add(layout);
    }

    private static Stream OpenQr() => typeof(DonationForm).Assembly.GetManifestResourceStream("LanChat.DonationQr")
        ?? throw new InvalidOperationException("Thiếu ảnh QR ủng hộ.");

    private static PictureBox QrPicture(Image image) => new()
    {
        Image = image, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill,
        AccessibleName = "Mã QR chuyển khoản ủng hộ phát triển"
    };

    private void ShowQr(Image image)
    {
        using var dialog = new DpiForm
        {
            Text = "Mã QR ủng hộ phát triển", Icon = AppIcon.Value, StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.FixedDialog, ClientSize = new Size(440, 800)
        };
        var layout = Theme.Stack(2);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(QrPicture(image), 0, 0);
        AddCloseButton(dialog, layout, 1);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }

    private static void AddCloseButton(Form form, TableLayoutPanel layout, int row)
    {
        var footer = Theme.Toolbar();
        footer.FlowDirection = FlowDirection.RightToLeft;
        var close = Theme.Button("Đóng");
        close.DialogResult = DialogResult.Cancel;
        form.CancelButton = close;
        footer.Controls.Add(close);
        layout.Controls.Add(footer, 0, row);
    }

    private void SaveQr(Label status)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Lưu ảnh QR ủng hộ", Filter = "Ảnh PNG (*.png)|*.png", DefaultExt = "png", FileName = "LanChat-ung-ho-qr.png"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var source = OpenQr();
            using var target = File.Create(dialog.FileName);
            source.CopyTo(target);
            status.Text = "Đã lưu ảnh QR.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Text = "Chưa lưu được ảnh QR. Hãy chọn một vị trí khác.";
        }
    }
}
