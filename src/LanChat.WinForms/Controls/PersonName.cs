using LanChat.Domain;
using CheckBoxState = System.Windows.Forms.VisualStyles.CheckBoxState;

namespace LanChat.WinForms.Controls;

internal readonly record struct PersonName
{
    public PersonName(string name, string? department = null)
    {
        Name = name;
        Department = department?.NormalizeLines(" ").Trim().ToUpperInvariant();
    }

    public string Name { get; }
    public string? Department { get; }
    public override string ToString() => string.IsNullOrEmpty(Department) ? Name ?? "" : $"{Department} {Name}";
}

internal sealed record SelectionItem(Guid Id, PersonName DisplayName)
{
    public SelectionItem(Guid id, string name, string? department = null) : this(id, new PersonName(name, department)) { }
    public override string ToString() => DisplayName.ToString();
}

internal sealed class PersonNameLabel : Label
{
    private PersonName _name = new("");
    internal PersonName Person => _name;

    public void SetName(PersonName name)
    {
        if (_name == name) return;
        _name = name;
        Text = name.ToString();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (string.IsNullOrEmpty(_name.Department))
        {
            base.OnPaint(e);
            return;
        }

        var area = new Rectangle(Padding.Left, Padding.Top, ClientSize.Width - Padding.Horizontal, ClientSize.Height - Padding.Vertical);
        PersonNameDrawing.Draw(e.Graphics, area, _name, Font, Enabled ? ForeColor : SystemColors.GrayText, TextAlign);
    }
}

internal static class PersonNameDrawing
{
    private const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    private const TextFormatFlags Ellipsis = Line | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping;

    public static void Draw(Graphics graphics, Rectangle bounds, PersonName person, Font font, Color color,
        ContentAlignment alignment = ContentAlignment.MiddleLeft)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || string.IsNullOrEmpty(person.Name)) return;
        var center = alignment == ContentAlignment.MiddleCenter;
        var middle = center || alignment == ContentAlignment.MiddleLeft;

        if (!string.IsNullOrEmpty(person.Department))
        {
            var smallCaps = Theme.GetFont(font.SizeInPoints * .78f, font.Bold);
            var gap = Math.Max(1, Measure(graphics, " ", font).Width);
            var nameSize = Measure(graphics, person.Name, font);
            var unitSize = Measure(graphics, person.Department!, smallCaps);
            var unitWidth = unitSize.Width;
            if (unitWidth + gap + nameSize.Width > bounds.Width)
                unitWidth = Math.Min(unitWidth, (bounds.Width - gap) * 2 / 5);

            if (unitWidth >= Measure(graphics, "…", smallCaps).Width)
            {
                var nameWidth = Math.Min(bounds.Width - unitWidth - gap, nameSize.Width);
                var x = center ? bounds.Left + (bounds.Width - unitWidth - gap - nameWidth) / 2 : bounds.Left;
                var nameTop = middle ? bounds.Top + Math.Max(0, (bounds.Height - nameSize.Height) / 2) : bounds.Top;
                var unitTop = nameTop + Ascent(font, nameSize.Height) - Ascent(smallCaps, unitSize.Height);
                if (unitTop < bounds.Bottom)
                    TextRenderer.DrawText(graphics, person.Department!, smallCaps,
                        Rectangle.FromLTRB(x, unitTop, x + unitWidth, bounds.Bottom), color, Ellipsis);
                TextRenderer.DrawText(graphics, person.Name, font,
                    Rectangle.FromLTRB(x + unitWidth + gap, nameTop, x + unitWidth + gap + nameWidth, bounds.Bottom), color, Ellipsis);
                return;
            }
        }

        var flags = Ellipsis;
        if (center) flags |= TextFormatFlags.HorizontalCenter;
        if (middle) flags |= TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(graphics, person.Name, font, bounds, color, flags);
    }

    private static Size Measure(Graphics graphics, string text, Font font) =>
        TextRenderer.MeasureText(graphics, text, font, Size.Empty, Line);

    private static int Ascent(Font font, int height) =>
        (int)Math.Round(height * (double)font.FontFamily.GetCellAscent(font.Style) / font.FontFamily.GetLineSpacing(font.Style));
}

internal sealed class PersonNameListBox : ListBox
{
    private readonly Func<object, PersonName> _getName;

    public PersonNameListBox(Func<object, PersonName> getName)
    {
        _getName = getName;
        DrawMode = DrawMode.OwnerDrawFixed;
        IntegralHeight = false;
        DpiChangedAfterParent += (_, _) => UpdateItemHeight();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateItemHeight();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateItemHeight();
    }

    private void UpdateItemHeight() => ItemHeight = Font.Height + this.ScalePixels(4);

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var area = Rectangle.Inflate(e.Bounds, -this.ScalePixels(3), 0);
        PersonNameDrawing.Draw(e.Graphics, area, _getName(Items[e.Index]), e.Font, e.ForeColor);
        e.DrawFocusRectangle();
    }
}

internal sealed class PersonNameCheckedListBox(Func<object, PersonName> getName) : CheckedListBox
{
    public Func<object, bool>? IsCheckEnabled { get; set; }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count)
        {
            base.OnDrawItem(e);
            return;
        }

        e.DrawBackground();
        var item = Items[e.Index];
        var canCheck = Enabled && IsCheckEnabled?.Invoke(item) != false;
        var state = GetItemCheckState(e.Index) switch
        {
            CheckState.Checked => canCheck ? CheckBoxState.CheckedNormal : CheckBoxState.CheckedDisabled,
            CheckState.Indeterminate => canCheck ? CheckBoxState.MixedNormal : CheckBoxState.MixedDisabled,
            _ => canCheck ? CheckBoxState.UncheckedNormal : CheckBoxState.UncheckedDisabled
        };
        var glyph = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
        var inset = this.ScalePixels(3);
        CheckBoxRenderer.DrawCheckBox(e.Graphics,
            new Point(e.Bounds.Left + inset, e.Bounds.Top + Math.Max(0, (e.Bounds.Height - glyph.Height) / 2)), state);
        var area = Rectangle.FromLTRB(e.Bounds.Left + glyph.Width + inset * 2, e.Bounds.Top, e.Bounds.Right - inset, e.Bounds.Bottom);
        PersonNameDrawing.Draw(e.Graphics, area, getName(item), e.Font, e.ForeColor);
        e.DrawFocusRectangle();
    }
}
