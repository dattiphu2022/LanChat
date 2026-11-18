namespace LanChat.WinForms.Controls;

internal sealed class UpdatePanel : UserControl
{
    private readonly UpdateCoordinator _updates;
    private readonly Label _message = Theme.Label("", 9);
    private readonly Button _check = Theme.Secondary("Kiểm tra cập nhật", UiIcon.Refresh);
    private readonly IconButton _apply = Theme.Button("Tải bản cập nhật", UiIcon.Download);
    private readonly Button _cancel = Theme.Secondary("Hủy tải", UiIcon.Close);
    private readonly ToolTip _messageTip = new();
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 18, Maximum = 1000,
        AccessibleName = "Tiến độ tải bản cập nhật" };
    public event Action? InstallRequested;
    internal IEnumerable<Button> ActionButtons => new Button[] { _check, _apply, _cancel };

    public UpdatePanel(UpdateCoordinator updates, bool showCheck = true)
    {
        _updates = updates;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top;
        Margin = Padding.Empty;
        var layout = Theme.Stack(3);
        layout.Dock = DockStyle.Top; layout.AutoSize = true; layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        for (var i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _message.UseMnemonic = false;
        _message.AutoSize = false; _message.AutoEllipsis = true;
        _message.AccessibleRole = AccessibleRole.StatusBar;
        var actions = Theme.Toolbar(); actions.Margin = Padding.Empty;
        actions.Controls.AddRange([_check, _apply, _cancel]);
        _check.Visible = showCheck;
        _check.Click += async (_, _) => await _updates.CheckAsync(true);
        _apply.Click += async (_, _) =>
        {
            if (_updates.State.Ready) InstallRequested?.Invoke();
            else await _updates.DownloadAsync();
        };
        _cancel.Click += (_, _) => _updates.CancelDownload();
        layout.Controls.Add(_message, 0, 0); layout.Controls.Add(_progress, 0, 1); layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);
        SizeChanged += (_, _) => FitMessage();
        _message.FontChanged += (_, _) => FitMessage();
        _updates.Changed += RefreshState;
        Disposed += (_, _) => { _updates.Changed -= RefreshState; _messageTip.Dispose(); };
        RefreshState();
    }

    private void RefreshState()
    {
        if (IsDisposed) return;
        var state = _updates.State;
        _message.Text = (state.Release is { } release ? $"LanChat {release.Version} · " : "") + state.Message;
        _messageTip.SetToolTip(_message, _message.Text);
        FitMessage();
        _check.Enabled = !state.Busy;
        _apply.Visible = state.Release is not null && !state.Downloading;
        _apply.Enabled = !state.Busy;
        _apply.Text = state.Ready ? "Cài đặt ngay" : "Tải bản cập nhật";
        _apply.Icon = state.Ready ? UiIcon.Install : UiIcon.Download;
        _progress.Visible = _cancel.Visible = state.Downloading;
        _cancel.Enabled = state.Downloading && !state.Message.StartsWith("Đang hủy", StringComparison.Ordinal);
        _progress.Value = state.Release is { Size: > 0 } item ? (int)Math.Max(0, Math.Min(1000, 1000d * state.Received / item.Size)) : 0;
    }

    private void FitMessage()
    {
        var width = Math.Max(1, ClientSize.Width - Padding.Horizontal - _message.Margin.Horizontal);
        var measured = TextRenderer.MeasureText(_message.Text, _message.Font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        _message.Size = new Size(width, Math.Min(_message.Font.Height * 4 + 2, Math.Max(_message.Font.Height, measured.Height)));
    }
}
