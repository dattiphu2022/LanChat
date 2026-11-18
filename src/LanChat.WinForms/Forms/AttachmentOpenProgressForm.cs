using LanChat.Application;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed class AttachmentOpenProgressForm : DpiForm
{
    private readonly Label _state = Theme.Label("Đang chuẩn bị tệp…");
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Maximum = 1000 };
    private readonly bool _forwarding;
    public AttachmentOpenProgressForm(string fileName, CancellationTokenSource cancellation, bool folder = false, bool forwarding = false)
    {
        _forwarding = forwarding;
        Icon = AppIcon.Value;
        Text = forwarding ? "Chuyển tiếp tin nhắn" : folder ? "Tải và mở thư mục" : "Mở tệp đính kèm";
        if (forwarding) _state.Text = "Đang chuẩn bị tệp và thư mục để chuyển tiếp…";
        else if (folder) _state.Text = "Đang đọc danh sách tệp trong thư mục…";
        ClientSize = new Size(500, 175); FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MaximizeBox = MinimizeBox = false;
        var layout = Theme.Stack(4); layout.Padding = new Padding(12);
        foreach (var height in new[] { 36, 36, 24, 52 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var name = Theme.Label(fileName, 11, true); name.AutoSize = false; name.AutoEllipsis = true;
        var cancel = Theme.Button("Hủy", UiIcon.Close); cancel.Click += (_, _) => Close();
        layout.Controls.Add(name, 0, 0); layout.Controls.Add(_state, 0, 1); layout.Controls.Add(_progress, 0, 2); layout.Controls.Add(cancel, 0, 3); Controls.Add(layout);
        FormClosing += (_, _) => cancellation.Cancel();
    }
    public void Report(FileProgress progress)
    {
        if (IsDisposed) return;
        if (progress.Completed >= progress.Total)
        { _progress.Style = ProgressBarStyle.Marquee; _state.Text = _forwarding ? "Đang kiểm tra và chuẩn bị chuyển tiếp…" : "Đang kiểm tra và chuẩn bị mở…"; }
        else
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = progress.Total == 0 ? 0 : (int)Math.Max(0, Math.Min(1000, progress.Completed * 1000 / progress.Total));
            _state.Text = $"Đang tải: {progress.Completed / 1024d:N0} / {progress.Total / 1024d:N0} KB";
        }
    }
}
