namespace LanChat.WinForms.Controls;

public sealed class AttachmentPreviewControl : UserControl
{
    private readonly PictureBox _image = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
    public event Action? RemoveRequested;
    public AttachmentPreviewControl(string path) : this(path, null) { }

    internal AttachmentPreviewControl(string path, Bitmap? thumbnail)
    {
        Size = new Size(260, 52); BackColor = Theme.Canvas;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var file = new FileInfo(path);
        var isFolder = Directory.Exists(path);
        var description = isFolder ? "Thư mục" : file.Exists ? $"{file.Length / 1024d:N1} KB" : "Không tìm thấy";
        var label = Theme.Label($"{name}\n{description}", 9); label.AutoSize = false; label.AutoEllipsis = true; label.UseMnemonic = false;
        var remove = Theme.Icon(UiIcon.Close, "Bỏ " + name);
        remove.Dock = DockStyle.Fill;
        remove.MinimumSize = Size.Empty;
        remove.Margin = new Padding(2, 4, 2, 4);
        remove.Padding = Padding.Empty;
        remove.Click += (_, _) => RemoveRequested?.Invoke();
        layout.Controls.Add(_image, 0, 0); layout.Controls.Add(label, 1, 0); layout.Controls.Add(remove, 2, 0); Controls.Add(layout);
        _image.Paint += (_, e) =>
        {
            if (_image.Image is not null) return;
            var size = this.ScalePixels(24);
            var icon = UiIcons.Get(isFolder ? UiIcon.Folder : UiIcon.File, size, Theme.Muted);
            e.Graphics.DrawImageUnscaled(icon, (_image.Width - size) / 2, (_image.Height - size) / 2);
        };
        if (thumbnail is not null) _image.Image = new Bitmap(thumbnail);
        if (thumbnail is null && !isFolder && file.Exists && file.Length < 20 * 1024 * 1024 &&
            new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp" }.Contains(file.Extension.ToLowerInvariant()))
        {
            try { using var original = Image.FromFile(path); _image.Image = new Bitmap(original, new Size(48, 48)); }
            catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException) { }
        }
    }
    protected override void Dispose(bool disposing) { if (disposing && !_image.IsDisposed) _image.Image?.Dispose(); base.Dispose(disposing); }
}
