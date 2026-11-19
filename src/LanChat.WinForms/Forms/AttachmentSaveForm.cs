using LanChat.Application;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed class AttachmentSaveForm : DpiForm
{
    private readonly Label _state = Theme.Label("Đang lưu…");
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000, Style = ProgressBarStyle.Marquee };
    private readonly PromptTextBox _results = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private bool _running = true;

    public AttachmentSaveForm(string title, Func<IProgress<AttachmentSaveProgress>, CancellationToken, Task<AttachmentSaveResult[]>> operation, CancellationToken lifetime)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var token = cancellation.Token;
        Text = "Lưu về máy — " + title; Icon = AppIcon.Value; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 350); MinimumSize = new Size(430, 280); ShowInTaskbar = false; MinimizeBox = MaximizeBox = false;
        var layout = Theme.Stack(4); layout.Padding = new Padding(12);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        UiInteraction.Wrap(_state, layout);
        var cancel = Theme.Button("Hủy", UiIcon.Close); CancelButton = cancel;
        void Cancel() { cancellation.Cancel(); cancel.Enabled = false; _state.Text = "Đang hủy… Các mục đã lưu được giữ nguyên."; }
        cancel.Click += (_, _) => { if (_running) Cancel(); else Close(); };
        layout.Controls.Add(_state, 0, 0); layout.Controls.Add(_progress, 0, 1); layout.Controls.Add(_results, 0, 2); layout.Controls.Add(cancel, 0, 3); Controls.Add(layout);
        FormClosing += (_, e) => { if (_running) { e.Cancel = true; Cancel(); } };
        var stopped = false;
        Disposed += (_, _) => { if (stopped) return; stopped = true; cancellation.Cancel(); cancellation.Dispose(); };
        Shown += async (_, _) =>
        {
            var progress = new Progress<AttachmentSaveProgress>(p =>
            {
                if (IsDisposed || !_running || cancellation.IsCancellationRequested) return;
                _state.Text = $"{p.Index}/{p.Count}: {p.FileName}";
                if (p.Bytes is { } current) _state.Text += " · " + AttachmentProgressText.Describe(current);
                else _state.Text += " · Đang lưu…";
                if (p.Bytes is { Total: > 0, Phase: AttachmentPhase.Downloading or AttachmentPhase.Saving } bytes)
                {
                    _progress.Style = ProgressBarStyle.Continuous; _progress.Value = (int)Math.Max(0, Math.Min(1000, bytes.Completed * 1000d / bytes.Total));

                }
                else _progress.Style = ProgressBarStyle.Marquee;
            });
            try
            {
                var results = await Task.Run(() => operation(progress, token));
                if (IsDisposed) return;
                _results.Text = string.Join(Environment.NewLine + Environment.NewLine, results.Select(r => r.Canceled ? "Đã hủy: " + r.File.FileName :
                    r.Error is not null ? "Lỗi: " + r.File.FileName + Environment.NewLine + UiText.HideIpAddresses(r.Error) : "Đã lưu: " + r.File.FileName + Environment.NewLine + r.Path));
                _state.Text = $"Đã lưu {results.Count(r => r.Path is not null)}/{results.Length} · Lỗi {results.Count(r => r.Error is not null)} · Đã hủy {results.Count(r => r.Canceled)}";
            }
            catch (OperationCanceledException) { if (!IsDisposed) _state.Text = "Đã hủy. Các mục đã lưu được giữ nguyên."; }
            catch (Exception ex) { if (!IsDisposed) _state.Text = "Chưa hoàn tất: " + UiText.HideIpAddresses(ex.Message); }
            finally { _running = false; if (!IsDisposed) { _progress.Visible = false; cancel.Enabled = true; cancel.Text = "Đóng"; cancel.Focus(); } }
        };
    }
}

internal sealed class AttachmentRecoveryForm : DpiForm
{
    public AttachmentRecoveryForm(string path)
    {
        Text = "Không tìm thấy bản đã lưu"; Icon = AppIcon.Value; ClientSize = new Size(620, 260); MinimumSize = new Size(440, 260);
        StartPosition = FormStartPosition.CenterParent; MinimizeBox = MaximizeBox = false;
        var layout = Theme.Stack(2); layout.Padding = new Padding(12);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var message = new PromptTextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Text = "Vị trí đã lưu không truy cập được:" + Environment.NewLine + path + Environment.NewLine + Environment.NewLine +
                "Tìm lại bản đang làm việc để giữ chỉnh sửa. Tạo lại từ bản gốc không phục hồi các chỉnh sửa cục bộ đã mất." };
        var buttons = Theme.Toolbar();
        var find = Theme.Button("Tìm lại tệp/thư mục…", UiIcon.Folder); find.DialogResult = DialogResult.Retry;
        var restore = Theme.Secondary("Tạo lại từ bản gốc", UiIcon.Restore); restore.DialogResult = DialogResult.Yes;
        buttons.Controls.AddRange([find, restore, UiInteraction.CloseButton(this, "Hủy")]);
        layout.Controls.Add(message, 0, 0); layout.Controls.Add(buttons, 0, 1); Controls.Add(layout); AcceptButton = find;
        Shown += (_, _) => find.Focus();
    }
}
