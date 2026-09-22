using LanChat.Application;
using LanChat.Domain;

namespace LanChat.WinForms.Controls;

public sealed class NavigationItemControl : UserControl
{
    private readonly Button _button = new() { Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand };
    public event EventHandler? Selected;
    public NavigationItemControl() { Size = new Size(145, 48); Padding = new Padding(5); Controls.Add(_button); _button.Click += (_, _) => Selected?.Invoke(this, EventArgs.Empty); }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Caption { get => _button.Text; set => _button.Text = value; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Active { set { _button.BackColor = value ? Theme.Blue : Color.White; _button.ForeColor = value ? Color.White : Theme.Ink; } }
    public void SetBadge(string title, int count) => Caption = count > 0 ? $"{title} ({count})" : title;
}

public sealed class UserItemControl : UserControl
{
    private readonly Label _title = Theme.Label("", 11, true);
    private readonly Label _detail = Theme.Label("");
    public Guid UserId { get; private set; }
    public event Action<Guid>? Selected;
    public UserItemControl()
    {
        Height = 78; Dock = DockStyle.Top; BackColor = Color.White; Cursor = Cursors.Hand;
        var stack = Theme.Stack(2); stack.Padding = new Padding(10); stack.Controls.Add(_title); stack.Controls.Add(_detail);
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _title.AutoSize = false; _title.AutoEllipsis = true;
        Controls.Add(stack); _title.Click += SelectUser; _detail.Click += SelectUser; stack.Click += SelectUser;
        _detail.ForeColor = Theme.Muted;
    }
    private void SelectUser(object? sender, EventArgs e) => Selected?.Invoke(UserId);
    public void Bind(UserSummary u)
    {
        UserId = u.UserId;
        _title.Text = $"{(u.Presence == PresenceStatus.Online ? "●" : "○")}  {u.DisplayName}";
        _detail.Text = $"{u.DeviceName}  ·  {u.IPAddress}\n{(u.Presence == PresenceStatus.Online ? "Đang trực tuyến" : u.LastSeenTicks > 0 ? "Lần cuối: " + new DateTime(u.LastSeenTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm") : "Chưa rõ trạng thái")}";
    }
}

public sealed class ConversationItemControl : UserControl
{
    private readonly Label _avatar = Theme.Label("", 16, true);
    private readonly Label _title = Theme.Label("", 11, true);
    private readonly Label _preview = Theme.Label("");
    private readonly Label _meta = Theme.Label("", 9);
    private ConversationSummary? _model;
    public event Action<ConversationSummary>? Selected;
    public event Action<ConversationSummary, string>? ActionRequested;
    public ConversationItemControl()
    {
        Height = 98; Dock = DockStyle.Top; Cursor = Cursors.Hand;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_avatar, 0, 0); layout.SetRowSpan(_avatar, 3);
        layout.Controls.Add(_title, 1, 0); layout.Controls.Add(_preview, 1, 1); layout.Controls.Add(_meta, 1, 2);
        _avatar.ForeColor = Theme.Blue; _preview.AutoEllipsis = true; _preview.AutoSize = false;
        _title.AutoSize = false; _title.AutoEllipsis = true; _meta.AutoSize = false; _meta.AutoEllipsis = true;
        _preview.ForeColor = _meta.ForeColor = Theme.Muted;
        Controls.Add(layout);
        var menu = new ContextMenuStrip();
        foreach (var pair in new[] { ("Ghim / Bỏ ghim", "pin"), ("Tắt / Bật thông báo", "mute"), ("Đánh dấu đã đọc", "read") })
            menu.Items.Add(pair.Item1, null, (_, _) => { if (_model is not null) ActionRequested?.Invoke(_model, pair.Item2); });
        ContextMenuStrip = menu;
        foreach (Control control in new Control[] { layout, _avatar, _title, _preview, _meta })
        { control.Click += (_, _) => { if (_model is not null) Selected?.Invoke(_model); }; control.ContextMenuStrip = menu; }
    }
    public void Bind(ConversationSummary c, bool active)
    {
        _model = c; _avatar.Text = string.Concat(c.DisplayName.Take(1)).ToUpperInvariant();
        _title.Text = c.DisplayName + (c.UnreadCount > 0 ? $"  ({c.UnreadCount})" : "");
        _preview.Text = c.Preview.ReplaceLineEndings(" ");
        _meta.Text = $"{(c.Kind == ConversationKind.Group ? "♧ Nhóm" : c.Presence == PresenceStatus.Online ? "● Online" : "○ Offline")}  {(c.IsPinned ? "◆ " : "")}{(c.IsMuted ? "Im lặng  " : "")}" +
            (c.LastMessageTicks > 0 ? new DateTime(c.LastMessageTicks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm") : "");
        BackColor = active ? Color.FromArgb(228, 237, 255) : Color.White;
    }
}

public sealed class ConversationListControl : UserControl
{
    private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Tìm cuộc trò chuyện…", Margin = new Padding(8) };
    private readonly Panel _items = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
    private readonly Dictionary<Guid, ConversationItemControl> _rows = [];
    private readonly Label _empty = Theme.Label("Chưa có cuộc trò chuyện.\nMở Người dùng để bắt đầu.");
    public string SearchText => _search.Text.Trim();
    public event Action<ConversationSummary>? Selected;
    public event Action<ConversationSummary, string>? ActionRequested;
    public ConversationListControl()
    {
        Dock = DockStyle.Fill; Padding = new Padding(8);
        var layout = Theme.Stack(3);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(Theme.Label("Tin nhắn", 16, true), 0, 0); layout.Controls.Add(_search, 0, 1); layout.Controls.Add(_items, 0, 2); Controls.Add(layout);
        _empty.Dock = DockStyle.Top; _empty.Padding = new Padding(10); _items.Controls.Add(_empty);
    }
    public void Bind(IReadOnlyList<ConversationSummary> conversations, Guid? activeId)
    {
        _items.SuspendLayout();
        foreach (var id in _rows.Keys.Except(conversations.Select(x => x.Id)).ToArray()) { _rows[id].Dispose(); _rows.Remove(id); }
        foreach (var c in conversations)
        {
            if (!_rows.TryGetValue(c.Id, out var row))
            {
                row = new ConversationItemControl(); row.Selected += x => Selected?.Invoke(x); row.ActionRequested += (x, a) => ActionRequested?.Invoke(x, a);
                _rows.Add(c.Id, row); _items.Controls.Add(row);
            }
            row.Bind(c, activeId == c.Id);
        }
        // Dock Top lays out in reverse z-order; update order without recreating controls.
        for (var i = 0; i < conversations.Count; i++) _items.Controls.SetChildIndex(_rows[conversations[i].Id], conversations.Count - i - 1);
        _empty.Visible = conversations.Count == 0; _items.ResumeLayout();
    }
}
