using LanChat.Application;

namespace LanChat.WinForms.Controls;

public sealed class ImageThumbnailControl : UserControl
{
    private readonly AttachmentDto _file;
    private readonly ChatImageService _images;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(228, 233, 240), Cursor = Cursors.Hand };
    private readonly Label _label = new() { Dock = DockStyle.Bottom, Height = 30, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Cursor = Cursors.Hand };
    private bool _loading;
    private DateTime _retryAfter;
    public bool HasImage => _picture.Image is not null;
    public event Action<AttachmentDto>? Selected;
    public ImageThumbnailControl(AttachmentDto file, ChatImageService images, bool compact = false)
    {
        _file = file; _images = images; Size = compact ? new Size(96, 66) : new Size(180, 165);
        if (compact) _label.Height = 22;
        Margin = new Padding(4); Padding = new Padding(2); BackColor = Color.White;
        _label.Text = file.FileSize > ChatImageService.InlineLimit ? "Bấm để xem ảnh lớn" : file.FileName;
        Controls.Add(_picture); Controls.Add(_label);
        _picture.Click += (_, _) => Selected?.Invoke(_file); _label.Click += (_, _) => Selected?.Invoke(_file);
        _picture.Paint += (_, _) => EnsureLoaded(); VisibleChanged += (_, _) => EnsureLoaded();
    }
    public void EnsureLoaded()
    {
        if (HasImage || _loading || IsDisposed || !IsHandleCreated || !Visible || DateTime.UtcNow < _retryAfter || _file.FileSize > ChatImageService.InlineLimit) return;
        var visible = RectangleToScreen(ClientRectangle);
        for (var parent = Parent; parent is not null; parent = parent.Parent) visible = Rectangle.Intersect(visible, parent.RectangleToScreen(parent.ClientRectangle));
        if (visible.Width < 1 || visible.Height < 1) return;
        _ = LoadAsync();
    }
    private async Task LoadAsync()
    {
        _loading = true; _label.Text = "Đang tải ảnh…";
        try
        {
            var bitmap = await _images.LoadAsync(_file, true, _lifetime.Token);
            if (IsDisposed || _lifetime.IsCancellationRequested) { bitmap.Dispose(); return; }
            _picture.Image = bitmap; _label.Text = _file.FileName;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { if (!IsDisposed) { _label.Text = "Chưa tải được · Bấm để thử"; _retryAfter = DateTime.UtcNow.AddSeconds(30); } }
        finally { _loading = false; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _lifetime.Cancel(); _picture.Image?.Dispose(); _picture.Image = null; }
        base.Dispose(disposing);
    }
}
