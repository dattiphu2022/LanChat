using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LanChat.WinForms.Controls;

// These code points use the Segoe MDL2 Assets font installed with Windows.
internal enum UiIcon
{
    NetworkUnread = 1,
    Network = 0xE968,
    Chat = 0xE8BD,
    Contacts = 0xE780,
    People = 0xE716,
    Folder = 0xED25,
    File = 0xE8A5,
    Scan = 0xE8FE,
    Search = 0xE721,
    Info = 0xE946,
    Settings = 0xE713,
    Send = 0xE724,
    Attach = 0xE723,
    Photo = 0xE91B,
    Paste = 0xE77F,
    Capture = 0xE7A8,
    Reply = 0x201C,
    Heart = 0xEB51,
    More = 0xE712,
    Close = 0xE711,
    Save = 0xE74E,
    AddPerson = 0xE8FA,
    Edit = 0xE70F,
    SwitchUser = 0xE748,
    Delete = 0xE74D,
    Download = 0xE896,
    Refresh = 0xE72C,
    Restore = 0xE7A7,
    Stop = 0xE71A,
    Install = 0xE7B8,
    Key = 0xE8D7,
    Storage = 0xEDA2,
    Diagnostics = 0xE9D9,
    Check = 0xE73E,
    Copy = 0xE8C8,
    ZoomIn = 0xE8A3,
    ZoomOut = 0xE71F,
    Fit = 0xE9A6,
    ActualSize = 0xE73F,
    Open = 0xE8A7,
    Previous = 0xE76B,
    Next = 0xE76C,
    Pin = 0xE718,
    Mute = 0xE74F,
    Add = 0xE710,
    AddNetwork = 0xE836,
    Exit = 0xE89B,
    QrCode = 0xED14,
    Forward = 0xE72A,
    Sync = 0xE895,
    Book = 0xE736,
    Up = 0xE74A,
    Down = 0xE74B
}

internal static class UiIcons
{
    private const string FontName = "Segoe MDL2 Assets";
    private static readonly Font IconFont = new(FontName, 16, GraphicsUnit.Pixel);
    private static readonly FontFamily IconFamily = IconFont.FontFamily;
    private static readonly FontFamily QuoteFamily = FontFamily.GenericSansSerif;
    private static readonly Dictionary<(UiIcon Icon, int Size, Color Color), Bitmap> Images = [];

    public static Bitmap ForMenu(UiIcon icon) => Get(icon, 32, SystemColors.MenuText);

    public static Bitmap Get(UiIcon icon, int size, Color color)
    {
        lock (Images)
        {
            var key = (icon, size, color);
            if (!Images.TryGetValue(key, out var image))
            {
                image = Render(icon, size, color);
                Images.Add(key, image);
            }
            return image;
        }
    }

    private static Bitmap Render(UiIcon icon, int size, Color color)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        using var path = new GraphicsPath();
        using var format = StringFormat.GenericTypographic;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var glyph = icon == UiIcon.NetworkUnread ? UiIcon.Network : icon;
        var quote = glyph == UiIcon.Reply;
        var family = quote ? QuoteFamily : IconFamily;
        var symbol = quote || family.Name == FontName ? (char)glyph : Fallback(glyph);
        path.AddString(symbol.ToString(), family, (int)(quote ? FontStyle.Bold : FontStyle.Regular), 96, PointF.Empty, format);
        var bounds = path.GetBounds();
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            var scale = size * .85f / Math.Max(bounds.Width, bounds.Height);
            var left = (size - bounds.Width * scale) / 2 - bounds.Left * scale;
            var top = (size - bounds.Height * scale) / 2 - bounds.Top * scale;
            using var transform = new Matrix(scale, 0, 0, scale, left, top);
            using var brush = new SolidBrush(color);
            path.Transform(transform);
            graphics.FillPath(brush, path);
        }
        if (icon == UiIcon.NetworkUnread)
        {
            var dot = Math.Max(4, size * 7 / 16);
            UnreadBadge.PaintDot(graphics, new Rectangle(size - dot, 0, dot, dot));
        }
        return bitmap;
    }

    private static char Fallback(UiIcon icon) => icon switch
    {
        UiIcon.Close => '×',
        UiIcon.Heart => '♥',
        UiIcon.More => '…',
        UiIcon.Previous => '‹',
        UiIcon.Next => '›',
        UiIcon.Up => '↑',
        UiIcon.Down => '↓',
        UiIcon.ZoomIn or UiIcon.Add => '+',
        UiIcon.ZoomOut => '−',
        UiIcon.Send => '➤',
        UiIcon.Stop => '■',
        UiIcon.Check => '✓',
        _ => '•'
    };
}

internal sealed class IconButton : Button
{
    private readonly int _iconSize;
    private UiIcon _icon;

    public UiIcon Icon
    {
        get => _icon;
        set
        {
            if (_icon == value) return;
            _icon = value;
            UpdateImage();
        }
    }

    public IconButton(UiIcon icon, int iconSize = 16)
    {
        _iconSize = iconSize;
        ImageAlign = TextAlign = ContentAlignment.MiddleCenter;
        TextImageRelation = TextImageRelation.ImageBeforeText;
        Icon = icon;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateImage(); }
    protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); UpdateImage(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateImage(); }
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        UpdateImage();
    }

    private void UpdateImage()
    {
        if (_iconSize == 0 || _icon == 0) return;
        var image = UiIcons.Get(_icon, this.ScalePixels(_iconSize), ForeColor);
        if (!ReferenceEquals(Image, image)) Image = image;
    }
}

internal sealed class IconTabControl : TabControl
{
    private readonly UiIcon[] _icons;
    private ImageList? _images;

    public IconTabControl(params UiIcon[] icons)
    {
        _icons = icons;
        UpdateImages();
    }

    public TabPage CreatePage(string text, UiIcon icon)
    {
        var page = new TabPage(text);
        SetIcon(page, icon);
        return page;
    }

    public void SetIcon(TabPage page, UiIcon icon)
    {
        var index = Array.IndexOf(_icons, icon);
        if (index < 0) throw new ArgumentException("The icon is missing from this tab control.", nameof(icon));
        if (page.ImageIndex != index) page.ImageIndex = index;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateImages(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateImages(); }
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        UpdateImages();
    }

    private void UpdateImages()
    {
        var size = this.ScalePixels(16);
        if (_images?.ImageSize.Width == size) return;
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(size, size) };
        foreach (var icon in _icons) images.Images.Add(UiIcons.Get(icon, size, SystemColors.ControlText));
        var previous = _images;
        _images = images;
        ImageList = images;
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ImageList = null;
            _images?.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class IconLinkLabel : LinkLabel
{
    private UiIcon _icon;

    public UiIcon Icon
    {
        get => _icon;
        set { if (_icon != value) { _icon = value; Invalidate(); } }
    }

    public IconLinkLabel(UiIcon icon)
    {
        _icon = icon;
        AutoSize = true;
        UseMnemonic = false;
        TextAlign = ContentAlignment.MiddleLeft;
        UpdatePadding();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdatePadding(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdatePadding(); }
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        UpdatePadding();
    }

    private void UpdatePadding() => Padding = new Padding(this.ScalePixels(19), 0, 0, 0);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Text.Length == 0) return;
        var size = this.ScalePixels(15);
        var left = 0;
        if (TextAlign == ContentAlignment.MiddleCenter)
        {
            var width = TextRenderer.MeasureText(e.Graphics, Text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width;
            left = Math.Max(0, (ClientSize.Width - Padding.Left - width) / 2);
        }
        var image = UiIcons.Get(_icon, size, Enabled ? LinkColor : DisabledLinkColor);
        e.Graphics.DrawImageUnscaled(image, left, (ClientSize.Height - size) / 2);
    }
}
