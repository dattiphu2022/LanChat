using LanChat.Application;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class AttachmentForm : Form
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _download;
    private readonly PictureBox _image = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Canvas };
    private readonly Label _state = Theme.Label("");
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000 };
    private string? _localPath;
    public AttachmentForm(AttachmentDto file, IFileTransferService files)
    {
        Text = file.FileName; ClientSize = new Size(660, 520); MinimumSize = new Size(480, 320); StartPosition = FormStartPosition.CenterParent;
        var layout = Theme.Stack(5); layout.Padding = new Padding(12);
        foreach (var height in new[] { 48, 36 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        var name = Theme.Label($"{file.FileName} · {file.FileSize / 1024d:N1} KB", 12, true); name.AutoSize = false; name.AutoEllipsis = true;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill }; var download = Theme.Button("Tải từ máy trong mạng"); var save = Theme.Button("Lưu thành…"); var cancel = Theme.Button("Hủy tải");
        save.Enabled = cancel.Enabled = false; buttons.Controls.AddRange([download, save, cancel]);
        layout.Controls.Add(name, 0, 0); layout.Controls.Add(_state, 0, 1); layout.Controls.Add(_image, 0, 2); layout.Controls.Add(_progress, 0, 3); layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
        void ShowPreview()
        {
            _state.Text = "Tệp đã có trên máy này"; save.Enabled = true;
            if (_localPath is not null && file.MimeType.StartsWith("image/", StringComparison.Ordinal) && file.FileSize <= 20 * 1024 * 1024)
            {
                try { using var source = Image.FromFile(_localPath); _image.Image?.Dispose(); _image.Image = new Bitmap(source); }
                catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException) { _state.Text = "Không thể xem trước ảnh này. Bạn vẫn có thể lưu tệp."; }
            }
        }
        Shown += async (_, _) => { try { _localPath = await files.GetLocalPathAsync(file.Sha256, _lifetime.Token); if (_localPath is not null) ShowPreview(); else _state.Text = "Bấm Tải để tìm máy đang giữ tệp."; } catch (Exception ex) { if (!IsDisposed) _state.Text = ex.Message; } };
        download.Click += async (_, _) =>
        {
            download.Enabled = false; cancel.Enabled = true; _download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            try
            {
                _state.Text = "Đang tìm nguồn và tải tệp…";
                var progress = new Progress<FileProgress>(p => { if (IsDisposed) return; _progress.Value = p.Total == 0 ? 1000 : (int)Math.Clamp(p.Completed * 1000 / p.Total, 0, 1000); _state.Text = $"Đã tải {p.Completed / 1024d:N0} / {p.Total / 1024d:N0} KB"; });
                _localPath = await files.DownloadAsync(file, progress, _download.Token); if (!IsDisposed) ShowPreview();
            }
            catch (OperationCanceledException) { if (!IsDisposed) _state.Text = "Đã hủy tải. Thông tin tệp vẫn được giữ lại."; }
            catch (Exception ex) { if (!IsDisposed) _state.Text = ex.Message; }
            finally { _download.Dispose(); _download = null; if (!IsDisposed) { download.Enabled = true; cancel.Enabled = false; } }
        };
        cancel.Click += (_, _) => _download?.Cancel();
        save.Click += async (_, _) =>
        {
            using var dialog = new SaveFileDialog { FileName = Path.GetFileName(file.FileName), OverwritePrompt = true };
            if (_localPath is null || dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var verified = await files.DownloadAsync(file, ct: _lifetime.Token);
                await using var source = File.OpenRead(verified); await using var output = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
                await source.CopyToAsync(output, _lifetime.Token); _state.Text = "Đã lưu tệp.";
            }
            catch (Exception ex) { if (!IsDisposed) _state.Text = ex.Message; }
        };
        FormClosed += (_, _) => { _lifetime.Cancel(); _download?.Cancel(); _image.Image?.Dispose(); };
    }
}
