using System.Diagnostics;
using LanChat.Application;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed record GalleryImage(AttachmentDto File, string Caption, string Sender, long CreatedAtTicks);

internal sealed class ImageGalleryForm : Form
{
    private readonly GalleryImage[] _items;
    private readonly ChatImageService _images;
    private readonly IFileTransferService _files;
    private readonly ImageViewportControl _viewport = new();
    private readonly Label _title = Theme.Label("", 12, true);
    private readonly Label _status = Theme.Label("", 9);
    private readonly Label _zoom = Theme.Label("100%", 9);
    private readonly TextBox _caption = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    private readonly FlowLayoutPanel _strip = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
    private readonly Button _previous = Theme.Button("‹ Trước");
    private readonly Button _next = Theme.Button("Sau ›");
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _selection;
    private int _index;
    private int _generation;
    public ImageGalleryForm(GalleryImage[] items, Guid selectedId, ChatImageService images, IFileTransferService files)
    {
        if (items.Length == 0) throw new ArgumentException("Cuộc trò chuyện chưa có ảnh.");
        _items = items; _images = images; _files = files; _index = Math.Max(0, Array.FindIndex(items, x => x.File.Id == selectedId));
        Text = "Xem ảnh — LanChat"; ClientSize = new Size(1080, 800); MinimumSize = new Size(780, 580); StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10); KeyPreview = true;
        var layout = Theme.Stack(6); layout.Padding = new Padding(12);
        foreach (var height in new[] { 54, -1, 30, 108, 86, 54 }) layout.RowStyles.Add(new RowStyle(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
        _title.AutoSize = _status.AutoSize = false; _title.AutoEllipsis = _status.AutoEllipsis = true;
        var captionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        captionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); captionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        captionLayout.Controls.Add(Theme.Label("Chú thích"), 0, 0); captionLayout.Controls.Add(_caption, 1, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        layout.SizeChanged += (_, _) => layout.RowStyles[5].Height = ClientSize.Width < 1020 ? 104 : 54;
        var zoomOut = Theme.Button("−"); var zoomIn = Theme.Button("＋"); zoomOut.MinimumSize = zoomIn.MinimumSize = new Size(42, 36);
        var fit = Theme.Button("Vừa khung"); var actual = Theme.Button("100%"); var external = Theme.Button("Ứng dụng ngoài"); var retry = Theme.Button("Tải lại");
        _zoom.Dock = DockStyle.None; _zoom.Padding = new Padding(0, 10, 0, 0); _zoom.Width = 60;
        buttons.Controls.AddRange([_previous, _next, zoomOut, zoomIn, fit, actual, _zoom, retry, external]);
        layout.Controls.Add(_title, 0, 0); layout.Controls.Add(_viewport, 0, 1); layout.Controls.Add(_status, 0, 2); layout.Controls.Add(_strip, 0, 3); layout.Controls.Add(captionLayout, 0, 4); layout.Controls.Add(buttons, 0, 5); Controls.Add(layout);
        _previous.Click += async (_, _) => await ShowImageAsync(_index - 1); _next.Click += async (_, _) => await ShowImageAsync(_index + 1);
        retry.Click += async (_, _) => await ShowImageAsync(_index);
        zoomOut.Click += (_, _) => _viewport.Zoom(1 / 1.25f); zoomIn.Click += (_, _) => _viewport.Zoom(1.25f); fit.Click += (_, _) => _viewport.Fit(); actual.Click += (_, _) => _viewport.ActualSize();
        _viewport.ZoomChanged += scale => _zoom.Text = $"{scale:P0}";
        external.Click += async (_, _) =>
        {
            external.Enabled = false;
            try
            {
                var path = await _files.PrepareOpenAsync(_items[_index].File, ct: _lifetime.Token);
                _lifetime.Token.ThrowIfCancellationRequested();
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open", WorkingDirectory = Path.GetDirectoryName(path) });
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { if (!IsDisposed) _status.Text = "Không mở được ứng dụng ngoài: " + ex.Message; }
            finally { if (!IsDisposed) external.Enabled = true; }
        };
        Shown += async (_, _) => { if (_generation == 0) await ShowImageAsync(_index); };
        FormClosed += (_, _) => { _lifetime.Cancel(); _selection?.Cancel(); };
    }
    private async Task ShowImageAsync(int index)
    {
        if (index < 0 || index >= _items.Length || _lifetime.IsCancellationRequested) return;
        _selection?.Cancel(); var selection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _selection = selection;
        var generation = ++_generation; _index = index; var item = _items[index]; _viewport.Image = null;
        _previous.Enabled = index > 0; _next.Enabled = index < _items.Length - 1;
        _title.Text = $"{index + 1} / {_items.Length}  ·  {item.File.FileName}\n{item.Sender}  ·  {new DateTime(item.CreatedAtTicks, DateTimeKind.Utc).ToLocalTime():dd/MM/yyyy HH:mm}";
        _caption.Text = item.Caption.ReplaceLineEndings(); _caption.PlaceholderText = "Không có chú thích"; _status.Text = "Đang tải ảnh…";
        foreach (Control control in _strip.Controls.Cast<Control>().ToArray()) control.Dispose();
        var first = Math.Max(0, Math.Min(index - 4, _items.Length - 9));
        for (var i = first; i < Math.Min(first + 9, _items.Length); i++)
        {
            var at = i; var thumbnail = new ImageThumbnailControl(_items[i].File, _images, true) { BorderStyle = BorderStyle.FixedSingle, BackColor = i == index ? Theme.Blue : Theme.Canvas };
            thumbnail.Selected += async _ => await ShowImageAsync(at); _strip.Controls.Add(thumbnail);
        }
        try
        {
            var progress = new Progress<FileProgress>(p => { if (!IsDisposed && generation == _generation) _status.Text = p.Completed >= p.Total ? "Đang đọc ảnh…" : $"Đang tải {p.Completed / 1024d:N0} / {p.Total / 1024d:N0} KB"; });
            var bitmap = await _images.LoadAsync(item.File, false, selection.Token, progress);
            if (IsDisposed || generation != _generation || selection.IsCancellationRequested) { bitmap.Dispose(); return; }
            _viewport.Image = bitmap; _status.Text = $"{bitmap.Width} × {bitmap.Height}  ·  Cuộn chuột để zoom · Kéo để di chuyển · ← → chuyển ảnh · Esc đóng";
        }
        catch (OperationCanceledException) when (selection.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed && generation == _generation) _status.Text = "Chưa xem được ảnh: " + ex.Message; }
        finally { if (ReferenceEquals(_selection, selection)) _selection = null; selection.Dispose(); }
    }
    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Left: _previous.PerformClick(); return true;
            case Keys.Right: _next.PerformClick(); return true;
            case Keys.Add: case Keys.Oemplus: _viewport.Zoom(1.25f); return true;
            case Keys.Subtract: case Keys.OemMinus: _viewport.Zoom(1 / 1.25f); return true;
            default: return base.ProcessCmdKey(ref msg, keyData);
        }
    }
#if DEBUG
    internal async Task VerifyAsync()
    {
        await ShowImageAsync(0); if (!_viewport.HasImage || _caption.Text != _items[0].Caption.ReplaceLineEndings()) throw new InvalidOperationException("Ảnh/chú thích đầu chưa hiển thị: " + _status.Text);
        var scale = _viewport.ScaleFactor; _viewport.Zoom(1.5f);
        if (_viewport.ScaleFactor <= scale) throw new InvalidOperationException("Phóng to không thay đổi tỷ lệ.");
        _viewport.ActualSize(); if (_viewport.ScaleFactor != 1) throw new InvalidOperationException("Sai tỷ lệ 100%.");
        _viewport.Fit();
        if (_items.Length < 2) throw new InvalidOperationException("Thiếu album nhiều ảnh.");
        await ShowImageAsync(1); if (!_viewport.HasImage || _caption.Text != _items[1].Caption.ReplaceLineEndings()) throw new InvalidOperationException("Không chuyển được ảnh/chú thích.");
        var first = ShowImageAsync(0); var last = ShowImageAsync(_items.Length - 1); await Task.WhenAll(first, last);
        if (!_viewport.HasImage || _index != _items.Length - 1 || _caption.Text != _items[^1].Caption.ReplaceLineEndings()) throw new InvalidOperationException("Ảnh tải cũ ghi đè lựa chọn mới.");
    }
    internal void VerifyLayout()
    {
        static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => Descendants(c).Prepend(c));
        if (_viewport.Height < 100 || _caption.Lines.Length != _items[_index].Caption.ReplaceLineEndings().Split(Environment.NewLine).Length) throw new InvalidOperationException("Viewport hoặc dòng chú thích không đúng.");
        foreach (var button in Descendants(this).OfType<Button>())
        {
            var bounds = button.RectangleToScreen(button.ClientRectangle); var visible = bounds;
            for (var parent = button.Parent; parent is not null; parent = parent.Parent) visible = Rectangle.Intersect(visible, parent.RectangleToScreen(parent.ClientRectangle));
            if (visible != bounds) throw new InvalidOperationException("Nút album bị khuất: " + button.Text);
        }
    }
#endif
}
