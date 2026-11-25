using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed class LicenseForm : DpiForm
{
    public LicenseForm()
    {
        Text = "Giấy phép MIT — LanChat";
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = ShowInTaskbar = false;
        ClientSize = new Size(640, 460);
        MinimumSize = new Size(380, 290);

        using var stream = typeof(LicenseForm).Assembly.GetManifestResourceStream("LanChat.License")
            ?? throw new InvalidOperationException("Thiếu nội dung giấy phép LanChat.");
        using var reader = new StreamReader(stream);
        var document = new RichTextBox
        {
            Text = reader.ReadToEnd(), Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, BackColor = Theme.Surface,
            ForeColor = Theme.Ink, ScrollBars = RichTextBoxScrollBars.Vertical, AccessibleName = "Nội dung giấy phép MIT",
            Margin = Padding.Empty
        };

        var layout = Theme.Stack(2);
        layout.Padding = new Padding(16, 12, 16, 8);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var footer = Theme.Toolbar();
        footer.FlowDirection = FlowDirection.RightToLeft;
        var close = Theme.Button("Đóng", UiIcon.Close);
        close.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(close);
        CancelButton = close;

        layout.Controls.Add(document, 0, 0);
        layout.Controls.Add(footer, 0, 1);
        Controls.Add(layout);
    }
}
