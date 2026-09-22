namespace LanChat.WinForms.Controls;

public sealed class AttachmentPreviewControl : UserControl
{
    private readonly PictureBox _image = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
    public event Action? RemoveRequested;
    public AttachmentPreviewControl(string path)
    {
        Size = new Size(260, 62); BackColor = Theme.Canvas;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
        var file = new FileInfo(path); var label = Theme.Label($"{file.Name}\n{file.Length / 1024d:N1} KB", 9); label.AutoSize = false; label.AutoEllipsis = true;
        var remove = new Button { Text = "×", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat }; remove.Click += (_, _) => RemoveRequested?.Invoke();
        layout.Controls.Add(_image, 0, 0); layout.Controls.Add(label, 1, 0); layout.Controls.Add(remove, 2, 0); Controls.Add(layout);
        _image.Image = SystemIcons.Application.ToBitmap();
        if (file.Length < 20 * 1024 * 1024 && new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp" }.Contains(file.Extension.ToLowerInvariant()))
        {
            try { using var original = Image.FromFile(path); var thumbnail = new Bitmap(original, new Size(48, 48)); _image.Image.Dispose(); _image.Image = thumbnail; }
            catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException) { }
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) _image.Image?.Dispose(); base.Dispose(disposing); }
}
