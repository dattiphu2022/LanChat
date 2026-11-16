using System.Drawing.Drawing2D;

namespace LanChat.WinForms.Controls;

internal sealed class UnreadBadge : Control
{
    private static readonly Color Red = Color.FromArgb(197, 31, 57);
    private static readonly Font BadgeFont = new("Segoe UI", 8.5f, FontStyle.Bold);

    public UnreadBadge()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Font = BadgeFont;
        Margin = new Padding(3, 0, 5, 0);
        Anchor = AnchorStyles.Right;
        TabStop = false;
        Visible = false;
        DpiChangedAfterParent += (_, _) => Size = GetPreferredSize(Size.Empty);
    }

    public void SetCount(int count)
    {
        Text = count <= 0 ? "" : count > 99 ? "99+" : count.ToString();
        AccessibleName = count > 0 ? $"{count} tin nhắn chưa đọc" : null;
        Size = GetPreferredSize(Size.Empty);
        Visible = count > 0;
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (Text.Length == 0) return Size.Empty;
        var height = Math.Max(this.ScalePixels(18), Font.Height + this.ScalePixels(3));
        var textWidth = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        return new Size(Math.Max(height, textWidth + this.ScalePixels(10)), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Text.Length == 0 || Width < 2 || Height < 2) return;
        var diameter = Math.Min(Height - 1, Width - 1);
        using var pill = new GraphicsPath();
        pill.AddArc(0, 0, diameter, diameter, 90, 180);
        pill.AddArc(Width - 1 - diameter, 0, diameter, diameter, 270, 180);
        pill.CloseFigure();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Red);
        e.Graphics.FillPath(brush, pill);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    internal static void PaintDot(Graphics graphics, Rectangle bounds, bool whiteOutline = false)
    {
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (whiteOutline) graphics.FillEllipse(Brushes.White, Rectangle.Inflate(bounds, 1, 1));
        using var brush = new SolidBrush(Red);
        graphics.FillEllipse(brush, bounds);
        graphics.SmoothingMode = smoothing;
    }
}
