namespace LanChat.WinForms.Controls;

internal static class Theme
{
    private static readonly Dictionary<(float Size, bool Bold), Font> Fonts = [];
    public static readonly Color Blue = Color.FromArgb(35, 100, 220);
    public static readonly Color Ink = Color.FromArgb(30, 42, 61);
    public static readonly Color Muted = Color.FromArgb(107, 120, 141);
    public static readonly Font DefaultFont = GetFont(10, false);
    public static readonly Color Rail = Color.FromArgb(21, 73, 159);
    public static readonly Color Border = Color.FromArgb(221, 229, 239);
    public static readonly Color Selection = Color.FromArgb(231, 241, 255);
    public static readonly Color Canvas = Color.FromArgb(245, 247, 251);
    public static Button Button(string text) => new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(90, 36),
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
        ForeColor = Ink, Font = GetFont(size, bold), Margin = new Padding(5, 1, 5, 1), Dock = DockStyle.Fill };

    public static PersonNameLabel NameLabel(string text, float size = 10, bool bold = false) => new()
    {
        Text = text, AutoEllipsis = true, UseMnemonic = false, ForeColor = Ink,
        Font = GetFont(size, bold), Margin = new Padding(5, 1, 5, 1), Dock = DockStyle.Fill
    };

    internal static Font GetFont(float size, bool bold)
    {
        var key = (size, bold);
        if (!Fonts.TryGetValue(key, out var font))
        {
            font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            Fonts.Add(key, font);
        }
        return font;
    }

    public static TableLayoutPanel Stack(int rows)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, BackColor = Color.White };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    public static FlowLayoutPanel Toolbar() => new BufferedFlowLayoutPanel
    {
        Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
    };

    // Controls inserted after a form has loaded missed its initial layout scaling pass.
    public static void AddAtCurrentDpi(Control parent, Control child)
    {
        var scale = parent.LayoutDpi() / 96f;
        if (scale != 1f) child.Scale(new SizeF(scale, scale));
        parent.Controls.Add(child);
    }

    // WinForms can report 96 DPI on a child HWND even after scaling its layout.
    public static int LayoutDpi(this Control control)
    {
        var form = control.FindForm();
        if (form is null) return control.DeviceDpi;
        if (form.AutoScaleMode != AutoScaleMode.Dpi) return 96;
        return (form.TopLevelControl ?? form).DeviceDpi;
    }

    public static int ScalePixels(this Control control, int logicalPixels) =>
        (int)Math.Round(logicalPixels * control.LayoutDpi() / 96d);
}

internal sealed class BufferedPanel : Panel
{
    public BufferedPanel() => DoubleBuffered = true;
}

internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    public BufferedFlowLayoutPanel() => DoubleBuffered = true;
}
