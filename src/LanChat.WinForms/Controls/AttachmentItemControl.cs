using System.Drawing.Drawing2D;
using LanChat.Application;

namespace LanChat.WinForms.Controls;

internal sealed class AttachmentItemControl : UserControl
{
    private readonly AttachmentDto _file;
    private readonly AttachmentActions? _actions;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly Button _open = CompactButton("Mở", UiIcon.Open);
    private readonly Button _save = CompactButton("Lưu về máy", UiIcon.Save);
    private readonly Label _location = Theme.Label("", 8);
    private readonly IconLinkLabel? _name;
    private readonly ImageThumbnailControl? _image;
    private readonly string _metadata;
    private int _locationRequest;
    private bool _stopped, _ready, _arranging;
    private (int Width, int Dpi) _fitSize;
    private ButtonMetrics _openMetrics, _saveMetrics;
    private record struct ButtonMetrics(Font Font, int IconWidth, int Padding, int Dpi, int Width);
    public event Action<AttachmentDto>? Selected;
    public event Action<AttachmentDto, AttachmentCommand>? CommandRequested;

    public AttachmentItemControl(AttachmentDto file, ChatImageService? images, AttachmentActions? actions)
    {
        _file = file; _actions = actions;
        // Inherit the solid bubble color so WinForms can erase this tile directly
        // instead of asking its parent to paint through a transparent background.
        DoubleBuffered = true; Margin = new Padding(0, 0, 0, 4);
        var folder = file.MimeType == FolderAttachment.MimeType;
        var extension = Path.GetExtension(file.FileName).TrimStart('.').ToUpperInvariant();
        _metadata = folder ? "Thư mục" : (extension.Length is > 0 and <= 10 ? extension + " · " : "") + FileSize(file.FileSize);
        if (images is not null && ChatImageService.IsImage(file))
        {
            _image = new ImageThumbnailControl(file, images, dense: true) { Margin = Padding.Empty };
            _image.Selected += f => Selected?.Invoke(f); Controls.Add(_image);
        }
        else
        {
            _name = new IconLinkLabel(folder ? UiIcon.Folder : UiIcon.File)
            {
                Text = file.FileName, AutoSize = false, AutoEllipsis = true, Margin = Padding.Empty,
                Font = Theme.GetFont(9, true), BackColor = Color.White, LinkColor = Theme.Ink,
                ActiveLinkColor = Theme.Blue, LinkBehavior = LinkBehavior.HoverUnderline
            };
            _name.LinkClicked += (_, _) => Selected?.Invoke(file); Controls.Add(_name);
            _tips.SetToolTip(_name, file.FileName);
        }
        _location.Text = _metadata; _location.AutoSize = false; _location.AutoEllipsis = true;
        _location.UseMnemonic = false; _location.Dock = DockStyle.None; _location.Margin = Padding.Empty; _location.ForeColor = Theme.Muted;
        _location.BackColor = Color.White; _location.Visible = _image is null;
        Controls.AddRange([_location, _open, _save]);
        _open.Click += (_, _) => CommandRequested?.Invoke(file, AttachmentCommand.Open);
        _save.Click += (_, _) => CommandRequested?.Invoke(file, AttachmentCommand.Save);
        _tips.SetToolTip(_save, "Lưu bản đang làm việc, gồm các chỉnh sửa cục bộ");
        if (actions is not null) { actions.Watch(file.Id); actions.Changed += OnChanged; }
        HandleCreated += async (_, _) => await RefreshLocationAsync();
        Disposed += (_, _) =>
        {
            if (_stopped) return; _stopped = true;
            if (actions is not null) { actions.Unwatch(file.Id); actions.Changed -= OnChanged; }
            _lifetime.Cancel(); _lifetime.Dispose(); _tips.Dispose();
        };
        _ready = true; FitWidth(_image is null ? 380 : 180);
    }

    private static Button CompactButton(string text, UiIcon icon)
    {
        var button = new IconButton(icon, 12)
        {
            Text = text, Font = Theme.GetFont(8.5f, false), FlatStyle = FlatStyle.Flat,
            BackColor = Color.White, ForeColor = Theme.Blue, Cursor = Cursors.Hand,
            Padding = new Padding(3, 0, 3, 0), Margin = Padding.Empty, UseMnemonic = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Theme.Selection;
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(213, 229, 252);
        return button;
    }
    private static string FileSize(long size) => size < 1024 ? $"{size:N0} B" : size < 1024 * 1024 ? $"{size / 1024d:N0} KB" : $"{size / (1024 * 1024d):N1} MB";
    private int ButtonWidth(Button button, ref ButtonMetrics cache)
    {
        var dpi = this.LayoutDpi();
        var icon = button.Image?.Width ?? this.ScalePixels(12);
        var padding = button.Padding.Horizontal;
        var font = button.Font;
        if (cache.Font != font || cache.IconWidth != icon || cache.Padding != padding || cache.Dpi != dpi)
            cache = new ButtonMetrics(font, icon, padding, dpi,
                TextRenderer.MeasureText(button.Text, font).Width + icon + padding + this.ScalePixels(8));
        return cache.Width;
    }
    private int ButtonHeight => Math.Max(this.ScalePixels(26), Math.Max(_open.Font.Height, _save.Font.Height) + this.ScalePixels(6));
    internal int ActionWidth => ButtonWidth(_open, ref _openMetrics) + ButtonWidth(_save, ref _saveMetrics) + this.ScalePixels(2);
    internal int PreferredRowWidth => ActionWidth + this.ScalePixels(24) + Math.Max(this.ScalePixels(130),
        Math.Min(this.ScalePixels(260), TextRenderer.MeasureText(_file.FileName, _name?.Font ?? Font).Width + this.ScalePixels(20)));
    private bool Stacked(int width) => width < ActionWidth + this.ScalePixels(_image is null ? 110 : 12);
    private bool StackButtons(int width) => width < ActionWidth + this.ScalePixels(_image is null ? 16 : 12);
    private int NameHeight => Math.Max(this.ScalePixels(18), (_name?.Font ?? Font).Height + this.ScalePixels(1));
    private int MetadataHeight => Math.Max(this.ScalePixels(14), _location.Font.Height);
    private int PreviewHeight => this.ScalePixels(128);
    internal int HeightForWidth(int width) => _image is not null
        ? PreviewHeight + ButtonHeight * (Stacked(width) ? 2 : 1) + this.ScalePixels(6)
        : Math.Max(NameHeight + MetadataHeight + this.ScalePixels(10), ButtonHeight + this.ScalePixels(10)) +
            (Stacked(width) ? ButtonHeight + this.ScalePixels(2) : 0) +
            (StackButtons(width) ? ButtonHeight : 0);

    internal void FitWidth(int width)
    {
        if (!_ready || _arranging) return;
        var size = (Width: width, Dpi: this.LayoutDpi());
        if (_fitSize == size && Width == width) return;
        _fitSize = size;
        _arranging = true;
        try { Size = new Size(Math.Max(1, width), HeightForWidth(width)); Arrange(); }
        finally { _arranging = false; }
    }
    private void Arrange()
    {
        var inset = this.ScalePixels(_image is null ? 8 : 3); var gap = this.ScalePixels(2);
        var openWidth = ButtonWidth(_open, ref _openMetrics); var saveWidth = ButtonWidth(_save, ref _saveMetrics); var height = ButtonHeight;
        var top = _image is null ? (Height - height) / 2 : PreviewHeight + this.ScalePixels(2);
        if (_image is not null) _image.SetBounds(inset, inset, Math.Max(1, Width - inset * 2), PreviewHeight - inset);
        if (_name is not null)
        {
            var textWidth = Math.Max(1, Width - inset * 2 - (Stacked(Width) ? 0 : ActionWidth + this.ScalePixels(8)));
            _name.SetBounds(inset, this.ScalePixels(4), textWidth, NameHeight);
            _location.SetBounds(inset + this.ScalePixels(19), _name.Bottom, Math.Max(1, textWidth - this.ScalePixels(19)), MetadataHeight);
            if (Stacked(Width)) top = _location.Bottom + this.ScalePixels(2);
        }
        if (StackButtons(Width))
        {
            _open.SetBounds(inset, top, Math.Max(1, Width - inset * 2), height);
            _save.SetBounds(inset, top + height, Math.Max(1, Width - inset * 2), height);
        }
        else
        {
            var left = Math.Max(inset, Width - inset - openWidth - saveWidth - gap);
            _open.SetBounds(left, top, openWidth, height); _save.SetBounds(_open.Right + gap, top, saveWidth, height);
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 3 || Height < 3) return;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var diameter = Math.Min(this.ScalePixels(8), Math.Min(bounds.Width, bounds.Height));
        using var shape = new GraphicsPath();
        shape.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        shape.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        shape.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        shape.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90); shape.CloseFigure();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(Color.White); using var edge = new Pen(Theme.Border);
        e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(edge, shape);
    }
    public void EnsureImageLoaded() => _image?.EnsureLoaded();
    private async void OnChanged(Guid id) { if (id == _file.Id) await RefreshLocationAsync(); }
    private async Task RefreshLocationAsync()
    {
        if (_actions is null || IsDisposed || _stopped) return;
        var request = ++_locationRequest;
        _open.Enabled = _save.Enabled = !_actions.IsBusy(_file.Id);
        if (!_open.Enabled) { _location.Text = _actions.State(_file.Id); return; }
        if (_actions.BackgroundState(_file.Id) is { } state) { _location.Text = state; return; }
        try
        {
            var location = await _actions.LocationAsync(_file, _lifetime.Token);
            if (IsDisposed || _lifetime.IsCancellationRequested || request != _locationRequest) return;
            _location.Text = _metadata + (location.UserSelected ? (location.Exists ? " · Đã lưu: " : " · Không tìm thấy: ") + location.Path : "");
            _tips.SetToolTip(_location, location.Path); _tips.SetToolTip(_open, "Mở: " + location.Path);
            _tips.SetToolTip(_save, "Lưu bản đang làm việc, gồm các chỉnh sửa cục bộ\n" + location.Path);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) { _location.Text = "Chưa đọc được vị trí"; _tips.SetToolTip(_location, ex.Message); } }
    }
}
