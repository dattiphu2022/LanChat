using LanChat.Application;

namespace LanChat.WinForms.Controls;

internal sealed class AttachmentItemControl : UserControl
{
    private readonly AttachmentDto _file;
    private readonly AttachmentActions? _actions;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly Button _open = Theme.Secondary("Mở", UiIcon.Open);
    private readonly Button _save = Theme.Secondary("Lưu về máy", UiIcon.Save);
    private readonly Label _location = Theme.Label("Bản làm việc LanChat", 8);
    private readonly ImageThumbnailControl? _image;
    private int _locationRequest;
    private bool _stopped;
    public event Action<AttachmentDto>? Selected;
    public event Action<AttachmentDto, AttachmentCommand>? CommandRequested;

    public AttachmentItemControl(AttachmentDto file, ChatImageService? images, AttachmentActions? actions)
    {
        _file = file; _actions = actions; Margin = new Padding(0, 4, 8, 4); Width = 180;
        var picture = images is not null && ChatImageService.IsImage(file);
        Height = picture ? 210 : 100; DoubleBuffered = true;
        var layout = Theme.Stack(3); layout.BackColor = Color.Transparent; layout.Margin = Padding.Empty;
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        if (picture)
        {
            _image = new ImageThumbnailControl(file, images!) { Dock = DockStyle.Fill, Margin = Padding.Empty };
            _image.Selected += f => Selected?.Invoke(f); layout.Controls.Add(_image, 0, 0);
        }
        else
        {
            var folder = file.MimeType == FolderAttachment.MimeType;
            var link = new IconLinkLabel(folder ? UiIcon.Folder : UiIcon.File)
            { Text = folder ? "Thư mục · " + file.FileName : file.FileName, Dock = DockStyle.Fill, AutoSize = false, AutoEllipsis = true, Margin = Padding.Empty };
            link.LinkClicked += (_, _) => Selected?.Invoke(file); layout.Controls.Add(link, 0, 0); _tips.SetToolTip(link, file.FileName);
        }
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35)); buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        foreach (var button in new[] { _open, _save })
        {
            button.AutoSize = false; button.Dock = DockStyle.Fill; button.MinimumSize = Size.Empty;
            button.Font = Theme.GetFont(8, false); button.Padding = new Padding(2, 1, 2, 1); button.Margin = new Padding(0, 2, 3, 0);
        }
        buttons.Controls.Add(_open, 0, 0); buttons.Controls.Add(_save, 1, 0);
        _location.AutoSize = false; _location.AutoEllipsis = true; _location.Margin = Padding.Empty; _location.ForeColor = Theme.Muted;
        layout.Controls.Add(buttons, 0, 1); layout.Controls.Add(_location, 0, 2); Controls.Add(layout);
        _open.Click += (_, _) => CommandRequested?.Invoke(file, AttachmentCommand.Open);
        _save.Click += (_, _) => CommandRequested?.Invoke(file, AttachmentCommand.Save);
        if (actions is not null) actions.Changed += OnChanged;
        HandleCreated += async (_, _) => await RefreshLocationAsync();
        Disposed += (_, _) => { if (_stopped) return; _stopped = true; if (actions is not null) actions.Changed -= OnChanged; _lifetime.Cancel(); _lifetime.Dispose(); _tips.Dispose(); };
    }
    public void EnsureImageLoaded() => _image?.EnsureLoaded();
    internal int ActionWidth
    {
        get
        {
            int WidthFor(Button button) => TextRenderer.MeasureText(button.Text, button.Font).Width + (button.Image?.Width ?? 0) +
                button.Padding.Horizontal + button.Margin.Horizontal + this.ScalePixels(14);
            return Math.Max((int)Math.Ceiling(WidthFor(_open) / .35), (int)Math.Ceiling(WidthFor(_save) / .65));
        }
    }
    private async void OnChanged(Guid id) { if (id == _file.Id) await RefreshLocationAsync(); }
    private async Task RefreshLocationAsync()
    {
        if (_actions is null || IsDisposed || _stopped) return;
        var request = ++_locationRequest;
        _open.Enabled = _save.Enabled = !_actions.IsBusy(_file.Id);
        if (!_open.Enabled) { _location.Text = "Đang xử lý…"; return; }
        try
        {
            var location = await _actions.LocationAsync(_file, _lifetime.Token);
            if (IsDisposed || _lifetime.IsCancellationRequested || request != _locationRequest) return;
            _location.Text = location.UserSelected ? (location.Exists ? "Đã lưu: " : "Không tìm thấy: ") + location.Path : "Bản làm việc LanChat";
            _tips.SetToolTip(_location, location.Path); _tips.SetToolTip(_open, "Mở: " + location.Path); _tips.SetToolTip(_save, "Lưu bản đang làm việc, gồm các chỉnh sửa cục bộ");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) { _location.Text = "Chưa đọc được vị trí"; _tips.SetToolTip(_location, ex.Message); } }
    }
}
