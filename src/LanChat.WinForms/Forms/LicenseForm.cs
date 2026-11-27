using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed class LicenseForm : DpiForm
{
    public LicenseForm(bool showThirdParty = false)
    {
        Text = showThirdParty ? "Giấy phép thư viện — LanChat" : "Giấy phép MIT — LanChat";
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = ShowInTaskbar = false;
        ClientSize = new Size(640, 460);
        MinimumSize = new Size(380, 290);

        var document = new RichTextBox
        {
            Text = showThirdParty ? ReadThirdPartyNotices() : ReadProjectLicense(),
            Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, BackColor = Theme.Surface,
            ForeColor = Theme.Ink, ScrollBars = RichTextBoxScrollBars.Vertical, AccessibleName = Text,
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

    private static string ReadProjectLicense()
    {
        using var stream = typeof(LicenseForm).Assembly.GetManifestResourceStream("LanChat.License")
            ?? throw new InvalidOperationException("Thiếu nội dung giấy phép LanChat.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ReadThirdPartyNotices()
    {
        try
        {
            return File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "THIRD-PARTY-NOTICES.txt"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Không đọc được THIRD-PARTY-NOTICES.txt. Hãy kiểm tra thư mục cài đặt LanChat.";
        }
    }
}
