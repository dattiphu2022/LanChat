namespace LanChat.WinForms.Controls;

internal sealed class UpdatePanel : UserControl
{
    private readonly UpdateCoordinator _updates;
    private readonly Label _message = Theme.Label("", 9);
    private readonly Button _check = Theme.Secondary("Kiểm tra cập nhật", UiIcon.Refresh);
    private readonly IconButton _apply = Theme.Button("Tải bản cập nhật", UiIcon.Download);
    private readonly Button _cancel = Theme.Secondary("Hủy tải", UiIcon.Close);
    private readonly Button _settings = Theme.Secondary("Cài đặt cập nhật…", UiIcon.Settings);
    private readonly ToolTip _messageTip = new();
    private readonly TableLayoutPanel _actions = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 2, RowCount = 4, Margin = Padding.Empty };
    private readonly bool _showCheck, _canConfigure;
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 18, Maximum = 1000,
        AccessibleName = "Tiến độ tải bản cập nhật" };
    public event Action? InstallRequested;
    internal IEnumerable<Button> ActionButtons => new Button[] { _check, _apply, _cancel, _settings };

    public UpdatePanel(UpdateCoordinator updates, bool showCheck = true, Action? configure = null)
    {
        _updates = updates;
        _showCheck = showCheck; _canConfigure = configure is not null;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top;
        Margin = Padding.Empty;
        var layout = Theme.Stack(3);
        layout.Dock = DockStyle.Top; layout.AutoSize = true; layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        for (var i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _message.UseMnemonic = false;
        _message.AutoSize = false; _message.AutoEllipsis = true;
        _message.AccessibleRole = AccessibleRole.StatusBar;
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) _actions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (var button in ActionButtons) { button.Anchor = AnchorStyles.Top | AnchorStyles.Left; _actions.Controls.Add(button); }
        _settings.Visible = configure is not null; _settings.Click += (_, _) => configure?.Invoke();
        _check.Visible = showCheck;
        _check.Click += async (_, _) => await _updates.CheckAsync(true);
        _apply.Click += async (_, _) =>
        {
            if (_updates.State.Ready) InstallRequested?.Invoke();
            else await _updates.DownloadAsync();
        };
        _cancel.Click += (_, _) => _updates.CancelDownload();
        layout.Controls.Add(_message, 0, 0); layout.Controls.Add(_progress, 0, 1); layout.Controls.Add(_actions, 0, 2);
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
        _message.Text = _updates.SourceName + " · " + (state.Release is { } release ? $"LanChat {release.Version} · " : "") + state.Message;
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
        // Explicit rows let the parent reserve both action lines at high DPI.
        // FlowLayoutPanel's preferred height can omit its wrapped second line.
        var visible = new List<Button>();
        if (_showCheck) visible.Add(_check);
        if (state.Release is not null && !state.Downloading) visible.Add(_apply);
        if (state.Downloading) visible.Add(_cancel);
        if (_canConfigure) visible.Add(_settings);
        _actions.SuspendLayout();
        for (var i = 0; i < visible.Count; i++) _actions.SetCellPosition(visible[i], new(i % 2, i / 2));
        var hidden = ActionButtons.Except(visible).ToArray();
        for (var i = 0; i < hidden.Length; i++) _actions.SetCellPosition(hidden[i], new(i % 2, 2 + i / 2));
        _actions.ResumeLayout(true);
    }

    private void FitMessage()
    {
        var width = Math.Max(1, ClientSize.Width - Padding.Horizontal - _message.Margin.Horizontal);
        var measured = TextRenderer.MeasureText(_message.Text, _message.Font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        _message.Size = new Size(width, Math.Min(_message.Font.Height * 4 + 2, Math.Max(_message.Font.Height, measured.Height)));
    }
}
