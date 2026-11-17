using LanChat.Application;

namespace LanChat.WinForms.Controls;

internal sealed class ReplyQuoteControl : Button
{
    private const TextFormatFlags PreviewFlags = TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
        TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
    private static readonly Font AuthorFont = Theme.GetFont(9, true);
    private static readonly Color PreviewInk = Color.FromArgb(65, 82, 108);
    private PersonName _author;
    private ReplyQuote? _quote;
    private bool _hover;
    private bool _own;

    public ReplyQuoteControl()
    {
        Dock = DockStyle.Fill;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.GetFont(9, false);
        Visible = TabStop = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Bind(ReplyQuote? quote, bool own)
    {
        if (_quote == quote && _own == own) return;
        _quote = quote;
        _own = own;
        _author = new PersonName(string.IsNullOrWhiteSpace(quote?.SenderName) ? "Người dùng" : quote!.SenderName!, quote?.SenderDepartment);
        Visible = quote is not null;
        Enabled = TabStop = quote?.CanNavigate == true;
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        AccessibleRole = quote?.CanNavigate == true ? AccessibleRole.PushButton : AccessibleRole.StaticText;
        AccessibleName = quote?.CanNavigate == true ? $"Đi đến tin nhắn của {_author}" : quote?.Preview;
        AccessibleDescription = quote?.CanNavigate == true ? quote.Preview : null;
        Invalidate();
    }

    public int HeightForWidth(int width)
    {
        if (_quote is null) return 0;
        return this.ScalePixels(12) + AuthorHeight + PreviewHeight(width);
    }

    private int AuthorHeight => _quote?.CanNavigate == true
        ? TextRenderer.MeasureText("Ág", AuthorFont, Size.Empty, TextFormatFlags.NoPadding).Height + this.ScalePixels(2) : 0;

    private int PreviewHeight(int width)
    {
        var available = Math.Max(1, width - this.ScalePixels(21));
        var line = TextRenderer.MeasureText("Ág", Font, Size.Empty, TextFormatFlags.NoPadding).Height;
        var flags = PreviewFlags & ~TextFormatFlags.EndEllipsis;
        var measured = TextRenderer.MeasureText(_quote?.Preview ?? "", Font, new Size(available, int.MaxValue), flags).Height;
        return Math.Max(line, Math.Min(measured, 2 * line));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_quote is not { } quote) return;
        var color = SystemInformation.HighContrast ? SystemColors.Control : _own
            ? Color.FromArgb(208, 226, 249) : Color.FromArgb(232, 237, 245);
        if (_hover && Enabled && !SystemInformation.HighContrast) color = ControlPaint.Dark(color, .08f);
        var accent = SystemInformation.HighContrast ? SystemColors.Highlight : quote.CanNavigate ? Theme.Blue : Theme.Border;
        using var background = new SolidBrush(color);
        using var brush = new SolidBrush(accent);
        e.Graphics.FillRectangle(background, ClientRectangle);
        e.Graphics.FillRectangle(brush, 0, 0, this.ScalePixels(3), Height);

        var left = this.ScalePixels(12);
        var top = this.ScalePixels(6);
        var width = Width - this.ScalePixels(21);
        if (width <= 0) return;
        var authorHeight = AuthorHeight;
        if (authorHeight > 0)
            PersonNameDrawing.Draw(e.Graphics, new Rectangle(left, top, width, authorHeight), _author, AuthorFont,
                SystemInformation.HighContrast ? SystemColors.ControlText : Theme.Ink);
        var preview = new Rectangle(left, top + authorHeight, width, Math.Max(0, Height - 2 * top - authorHeight));
        if (preview.Height > 0)
            TextRenderer.DrawText(e.Graphics, quote.Preview, Font, preview,
                SystemInformation.HighContrast ? SystemColors.ControlText : PreviewInk, PreviewFlags);
        if (Focused && ShowFocusCues && Width > 6 && Height > 6)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData != Keys.Enter || !Focused || !Enabled) return base.ProcessDialogKey(keyData);
        PerformClick();
        return true;
    }
}
