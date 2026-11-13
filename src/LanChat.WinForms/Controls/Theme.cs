namespace LanChat.WinForms.Controls;

internal static class Theme
{
    public static readonly Color Blue = Color.FromArgb(35, 100, 220);
    public static readonly Color Ink = Color.FromArgb(30, 42, 61);
    public static readonly Color Muted = Color.FromArgb(107, 120, 141);
    public static readonly Font SmallFont = new("Segoe UI", 8.5f);
    public static readonly Color Rail = Color.FromArgb(21, 73, 159);
    public static readonly Color Border = Color.FromArgb(221, 229, 239);
    public static readonly Color Selection = Color.FromArgb(231, 241, 255);
    public static readonly Color Canvas = Color.FromArgb(245, 247, 251);
    public static Button Button(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(90, 36),
        FlatStyle = FlatStyle.Flat, BackColor = Blue, ForeColor = Color.White, Cursor = Cursors.Hand, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(5) };
    public static Button Secondary(string text)
    {
        var button = Button(text);
        button.BackColor = Color.White;
        button.ForeColor = Ink;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Selection;
        return button;
    }
    public static Label Label(string text, float size = 10, bool bold = false) => new() { Text = text, AutoSize = true,
        ForeColor = Ink, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(5, 1, 5, 1), Dock = DockStyle.Fill };
    public static TableLayoutPanel Stack(int rows)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, BackColor = Color.White };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }
}
