using LanChat.Domain;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed class UserGuideForm : DpiForm
{
    public UserGuideForm()
    {
        Text = "Hướng dẫn sử dụng LanChat";
        Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = ShowInTaskbar = false;
        ClientSize = new Size(640, 460);
        MinimumSize = new Size(380, 290);

        using var stream = typeof(UserGuideForm).Assembly.GetManifestResourceStream("LanChat.UserGuide")
            ?? throw new InvalidOperationException("Thiếu tài liệu hướng dẫn sử dụng.");
        using var reader = new StreamReader(stream);
        var chapters = reader.ReadToEnd().NormalizeLines("\n").Split(["\n## "], StringSplitOptions.None)
            .Skip(1).Select(section => section.Split(['\n'], 2)).ToArray();

        var layout = Theme.Stack(3);
        layout.Padding = new Padding(16, 12, 16, 8);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var topics = new ComboBox
        {
            Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = "Chủ đề hướng dẫn", Margin = new Padding(0, 0, 0, 10)
        };
        topics.Items.AddRange(chapters.Select(chapter => chapter[0].Trim()).ToArray());

        var document = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, BackColor = Theme.Surface,
            ForeColor = Theme.Ink, ScrollBars = RichTextBoxScrollBars.Vertical, AccessibleName = "Nội dung hướng dẫn", Margin = Padding.Empty
        };
        topics.SelectedIndexChanged += (_, _) =>
        {
            if (topics.SelectedIndex < 0) return;
            var body = chapters[topics.SelectedIndex][1].Trim().Replace("`", "").Replace("\n- ", "\n• ");
            if (body.StartsWith("- ", StringComparison.Ordinal)) body = "• " + body.Substring(2);
            var runs = body.Split(["**"], StringSplitOptions.None);
            document.Clear();
            for (var i = 0; i < runs.Length; i++)
            {
                document.SelectionFont = Theme.GetFont(10, bold: i % 2 == 1);
                document.AppendText(runs[i]);
            }
            document.Select(0, 0);
            document.ScrollToCaret();
        };

        var footer = Theme.Toolbar();
        footer.FlowDirection = FlowDirection.RightToLeft;
        var close = Theme.Button("Đóng", UiIcon.Close);
        close.DialogResult = DialogResult.Cancel;
        footer.Controls.Add(close);
        CancelButton = close;
        layout.Controls.Add(topics, 0, 0);
        layout.Controls.Add(document, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        topics.SelectedIndex = 0;
    }
}
