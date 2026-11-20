using LanChat.Application;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class AttachmentForm : DpiForm
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private readonly PictureBox _image = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Canvas };
    private readonly Label _state = Theme.Label("");
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Visible = false, AccessibleName = "Tiến độ tệp" };
    private string? _localPath;
    private bool _closed, _loading = true;
    private bool _ownsActions;

    public AttachmentForm(AttachmentDto file, IFileTransferService files) : this(file, files, new AttachmentActions(files)) { _ownsActions = true; }

    internal AttachmentForm(AttachmentDto file, IFileTransferService files, AttachmentActions actions)
    {
        Icon = AppIcon.Value;
        Text = file.FileName; ClientSize = new Size(700, 540); MinimumSize = new Size(480, 320); StartPosition = FormStartPosition.CenterParent;
        var layout = Theme.Stack(5); layout.Padding = new Padding(12);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var name = Theme.Label($"{file.FileName} · {file.FileSize / 1024d:N1} KB", 12, true);
        UiInteraction.Wrap(name, layout); UiInteraction.Wrap(_state, layout);
        var buttons = Theme.Toolbar(); var download = Theme.Button("Tải từ máy trong mạng", UiIcon.Download);
        var save = Theme.Button("Lưu về máy", UiIcon.Save); var open = Theme.Button("Mở", UiIcon.Open); var cancel = Theme.Secondary("Hủy tải", UiIcon.Close);
        buttons.Controls.AddRange([open, save, download, cancel, UiInteraction.CloseButton(this)]);
        layout.Controls.Add(name, 0, 0); layout.Controls.Add(_state, 0, 1); layout.Controls.Add(_image, 0, 2); layout.Controls.Add(_progress, 0, 3); layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
        void UpdateButtons()
        {
            var busy = _operation is not null;
            download.Enabled = !_loading && !busy;
            open.Enabled = save.Enabled = !_loading && !busy && !actions.IsBusy(file.Id);
            cancel.Visible = busy; cancel.Enabled = busy && !_operation!.IsCancellationRequested;
            _progress.Visible = busy;
        }
        void ShowPreview()
        {
            _state.Text = "Tệp đã có trên máy này.";
            if (_localPath is not null && file.MimeType.StartsWith("image/", StringComparison.Ordinal) && file.FileSize <= 20 * 1024 * 1024)
            {
                try { using var source = Image.FromFile(_localPath); _image.Image?.Dispose(); _image.Image = new Bitmap(source); }
                catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException) { _state.Text = "Không thể xem trước ảnh này. Bạn vẫn có thể lưu tệp."; }
            }
        }
        Shown += async (_, _) =>
        {
            _state.Text = "Đang kiểm tra tệp trên máy…";
            try
            {
                var local = await files.GetLocalPathAsync(file.Sha256, _lifetime.Token);
                if (_closed) return;
                _localPath = local;
                if (local is not null) ShowPreview(); else _state.Text = "Bấm Tải để tìm máy đang giữ tệp.";
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { if (!_closed) _state.Text = UiText.HideIpAddresses(ex.Message); }
            finally { _loading = false; if (!_closed) { UpdateButtons(); await ShowLocation(); } }
        };
        download.Click += async (_, _) =>
        {
            if (_loading || _operation is not null || _closed) return;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _operation = cancellation; cancel.Text = "Hủy tải"; _progress.Value = 0; UpdateButtons();
            try
            {
                _state.Text = "Đang tìm nguồn và tải tệp…";
                var progress = new Progress<FileProgress>(p =>
                {
                    if (_closed || !ReferenceEquals(_operation, cancellation) || cancellation.IsCancellationRequested) return;
                    _progress.Value = p.Total <= 0 ? 0 : (int)Math.Max(0, Math.Min(1000, p.Completed * 1000d / p.Total));
                    _state.Text = $"Đã tải {p.Completed / 1024d:N0} / {p.Total / 1024d:N0} KB";
                });
                var local = await files.DownloadAsync(file, progress, cancellation.Token);
                if (_closed || cancellation.IsCancellationRequested) return;
                _localPath = local; ShowPreview(); await ShowLocation();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { if (!_closed) _state.Text = "Đã hủy tải. Bạn có thể tải lại."; }
            catch (Exception ex) { if (!_closed) _state.Text = UiText.HideIpAddresses(ex.Message); }
            finally { _operation = null; if (!_closed) UpdateButtons(); }
        };
        cancel.Click += (_, _) => { _operation?.Cancel(); _state.Text = "Đang hủy…"; UpdateButtons(); };
        async Task ShowLocation()
        {
            try
            {
                var location = await actions.LocationAsync(file, _lifetime.Token);
                if (!_closed) _state.Text = location.UserSelected ? (location.Exists ? "Đã lưu: " : "Không tìm thấy: ") + location.Path : "Bản làm việc: " + location.Path;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!_closed) _state.Text = UiText.HideIpAddresses(ex.Message); }
        }
        open.Click += async (_, _) => { if (!open.Enabled) return; open.Enabled = save.Enabled = false; await actions.OpenAsync(this, file, _lifetime.Token); if (!_closed) { UpdateButtons(); await ShowLocation(); } };
        save.Click += async (_, _) => { if (!save.Enabled) return; open.Enabled = save.Enabled = false; await actions.SaveAsync(this, file, _lifetime.Token); if (!_closed) { UpdateButtons(); await ShowLocation(); } };
        void Stop()
        {
            if (_closed) return;
            _closed = true; _lifetime.Cancel(); _operation?.Cancel(); _image.Image?.Dispose(); _image.Image = null;
        }
        FormClosed += (_, _) => Stop();
        Disposed += (_, _) => { Stop(); _lifetime.Dispose(); if (_ownsActions) actions.Dispose(); };
        UpdateButtons();
    }
}
