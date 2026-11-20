using LanChat.Application;
using LanChat.Domain;

namespace LanChat.WinForms.Controls;

public sealed class NavigationItemControl : UserControl
{
    private readonly IconButton _button = new(UiIcon.Chat, 18)
    {
        Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, TextImageRelation = TextImageRelation.ImageAboveText,
        Cursor = Cursors.Hand, ForeColor = Color.White, BackColor = Theme.Rail
    };
    private int _unreadCount;
    private string _caption = "";
    private bool _compact;
    private readonly ToolTip _tips = new();
    internal void SetCompact(bool compact) { _compact = compact; var text = compact ? "" : _caption; if (_button.Text != text) _button.Text = text; }
    public event EventHandler? Selected;
    public NavigationItemControl()
    {
        _button.FlatAppearance.BorderSize = 0; _button.FlatAppearance.MouseOverBackColor = Theme.Blue;
        Size = new Size(102, 54); Padding = new Padding(5); Controls.Add(_button);
        _button.Click += (_, _) => Selected?.Invoke(this, EventArgs.Empty);
        Disposed += (_, _) => _tips.Dispose();
        _button.Paint += (_, e) =>
        {
            if (_unreadCount <= 0) return;
            var size = this.ScalePixels(9);
            UnreadBadge.PaintDot(e.Graphics, new Rectangle(_button.ClientSize.Width - size - this.ScalePixels(4),
                this.ScalePixels(3), size, size));
        };
    }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Caption { get => _caption; set { _caption = value; _button.Text = _compact ? "" : value; _button.AccessibleName = value; _tips.SetToolTip(_button, value); } }
    internal UiIcon Icon { get => _button.Icon; set => _button.Icon = value; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Active { set { _button.BackColor = value ? Theme.Blue : Theme.Rail; _button.ForeColor = Color.White; } }
    internal void SetUnreadCount(int count)
    {
        if (_unreadCount == count) return;
        _unreadCount = count;
        _button.AccessibleName = count > 0 ? $"{Caption}, {count} tin nhắn chưa đọc" : Caption;
        _button.Invalidate();
    }
}

public sealed class UserItemControl : UserControl
{
    private readonly PersonNameLabel _title = Theme.NameLabel("", 11, true);
    private readonly Label _device = Theme.Label("", 9);
    private readonly Label _presence = Theme.Label("", 9);
    public Guid UserId { get; private set; }
    public event Action<Guid>? Selected;
    public UserItemControl()
    {
        Height = 78; TabStop = true; AccessibleRole = AccessibleRole.ListItem; Dock = DockStyle.Top; BackColor = Color.White; Cursor = Cursors.Hand;
        var stack = Theme.Stack(3);
        stack.Padding = new Padding(10, 6, 10, 6);
        for (var row = 0; row < 3; row++) stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _device.ForeColor = _presence.ForeColor = Theme.Muted;
        foreach (var label in new[] { _title, _device, _presence })
        {
            label.AutoSize = false;
            label.AutoEllipsis = true;
            label.UseMnemonic = false;
            label.Click += SelectUser;
            stack.Controls.Add(label);
        }
        KeyDown += (_, e) => { if (e.KeyCode is Keys.Enter or Keys.Space) { Selected?.Invoke(UserId); e.SuppressKeyPress = true; } };
        stack.Paint += (_, e) => { if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(stack.ClientRectangle,-2,-2),Theme.Blue,BackColor); };
        GotFocus += (_, _) => stack.Invalidate(); LostFocus += (_, _) => stack.Invalidate();
        stack.Click += SelectUser;
        Controls.Add(stack);
        void FitRow()
        {
            var height = stack.Controls.OfType<Label>().Sum(label => label.PreferredHeight + label.Margin.Vertical);
            Height = Math.Max(this.ScalePixels(78), height + stack.Padding.Vertical);
        }
        foreach (var label in new[] { _title, _device, _presence }) label.FontChanged += (_, _) => FitRow();
        DpiChangedAfterParent += (_, _) => FitRow();
    }
    private void SelectUser(object? sender, EventArgs e) => Selected?.Invoke(UserId);
    public void Bind(UserSummary u)
    {
        UserId = u.UserId;
        AccessibleName = new PersonName(u.DisplayName,u.Department).ToString();
        _title.SetName(new PersonName(u.DisplayName, u.Department));
        _device.Text = UiText.HideIpAddresses(u.DeviceName);
        _presence.Text = u.Presence == PresenceStatus.Online ? "● Đang trực tuyến" : u.LastSeenTicks > 0
            ? $"○ Lần cuối: {new DateTime(u.LastSeenTicks, DateTimeKind.Utc).ToLocalTime():dd/MM HH:mm}" : "○ Chưa rõ trạng thái";
    }
}

public sealed class ConversationItemControl : UserControl
{
    private readonly Label _avatar = Theme.Label("", 16, true);
    private readonly PersonNameLabel _title = Theme.NameLabel("", 11, true);
    private readonly Label _preview = Theme.Label("");
    private readonly Label _meta = Theme.Label("", 9);
    private readonly UnreadBadge _unread = new();
    private readonly Label _time = Theme.Label("", 8);
    private readonly TableLayoutPanel _layout = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Padding = new Padding(8, 5, 8, 5) };
    private UiPreferences _appearance = new();

    internal void ApplyPreferences(UiPreferences preferences)
    {
        _appearance = preferences;
        FitRow();
    }

    private void FitRow()
    {
        var title = Math.Max(_title.PreferredHeight + _title.Margin.Vertical, _time.PreferredHeight + _time.Margin.Vertical);
        var preview = _preview.PreferredHeight + _preview.Margin.Vertical;
        var badgeSize = _unread.GetPreferredSize(Size.Empty);
        var meta = Math.Max(_meta.PreferredHeight + _meta.Margin.Vertical, badgeSize.Height + _unread.Margin.Vertical);

        _layout.SuspendLayout();
        try
        {
            _unread.Size = badgeSize;
            _layout.RowStyles[0].Height = title;
            _layout.RowStyles[1].Height = preview;
            Height = Math.Max(this.ScalePixels(_appearance.ConversationRowHeight), title + preview + meta + _layout.Padding.Vertical + Padding.Vertical);
        }
        finally { _layout.ResumeLayout(true); }
    }
    private ConversationSummary? _model;
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    public event Action<ConversationSummary>? Selected;
    public event Action<ConversationSummary, string>? ActionRequested;
    public ConversationItemControl()
    {
        Height = 72; TabStop = true; Dock = DockStyle.Top; Cursor = Cursors.Hand;
        DoubleBuffered = true; ResizeRedraw = true;
        Padding = new Padding(0, 0, 0, 1);
        DpiChangedAfterParent += (_, _) => { Padding = new Padding(0, 0, 0, 1); FitRow(); };
        var layout = _layout;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 21)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_avatar, 0, 0); layout.SetRowSpan(_avatar, 3);
        layout.Controls.Add(_title, 1, 0); layout.Controls.Add(_preview, 1, 1); layout.Controls.Add(_meta, 1, 2);
        layout.Controls.Add(_time, 2, 0); layout.Controls.Add(_unread, 2, 2);
        _time.TextAlign = ContentAlignment.MiddleRight; _time.ForeColor = Theme.Muted;
        _time.Margin = new Padding(2, 1, 2, 1);
        layout.SetColumnSpan(_preview, 2); layout.SetColumnSpan(_meta, 1);
        _avatar.ForeColor = Theme.Blue; _preview.AutoEllipsis = true; _preview.AutoSize = false;
        _title.AutoSize = false; _title.AutoEllipsis = true; _meta.AutoSize = false; _meta.AutoEllipsis = true;
        _avatar.UseMnemonic = _title.UseMnemonic = _preview.UseMnemonic = _meta.UseMnemonic = false;
        _preview.ForeColor = _meta.ForeColor = Theme.Muted;
        Controls.Add(layout);
        layout.Paint += (_, e) => { if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(layout.ClientRectangle, -2, -2), Theme.Blue, BackColor); };
        GotFocus += (_, _) => layout.Invalidate(); LostFocus += (_, _) => layout.Invalidate();
        var menu = new ContextMenuStrip();
        Disposed += (_, _) => menu.Dispose();
        KeyDown += (_, e) => { if ((e.KeyCode is Keys.Enter or Keys.Space) && _model is not null) { Selected?.Invoke(_model); e.SuppressKeyPress = true; } };
        foreach (var (caption, action, icon) in new[] { ("Ghim / Bỏ ghim", "pin", UiIcon.Pin),
            ("Tắt / Bật thông báo", "mute", UiIcon.Mute), ("Đánh dấu đã đọc", "read", UiIcon.Check) })
            menu.Items.Add(caption, UiIcons.ForMenu(icon), (_, _) => { if (_model is not null) ActionRequested?.Invoke(_model, action); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Xóa hội thoại phía tôi…", UiIcons.ForMenu(UiIcon.Delete), (_, _) => { if (_model is not null) ActionRequested?.Invoke(_model, "delete"); });
        menu.Opening += (_, e) => e.Cancel = _model?.Id == Guid.Empty;
        ContextMenuStrip = menu;
        foreach (Control control in new Control[] { layout, _avatar, _title, _preview, _meta, _unread, _time })
        { control.Click += (_, _) => { if (_model is not null) Selected?.Invoke(_model); }; control.ContextMenuStrip = menu; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var inset = this.ScalePixels(8);
        using var line = new SolidBrush(Theme.Border);
        e.Graphics.FillRectangle(line, inset, ClientSize.Height - 1, Math.Max(0, ClientSize.Width - inset * 2), 1);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3), Theme.Ink, BackColor);
    }
    internal void SetActive(bool active)
    {
        var color = active ? Theme.Selection : Color.White;
        if (BackColor != color) BackColor = color;
    }
    public void Bind(ConversationSummary c, bool active)
    {
        if (_model == c && BackColor == (active ? Theme.Selection : Color.White)) return;
        _model = c; _avatar.Text = string.Concat(c.DisplayName.Trim().Take(1)).ToUpperInvariant();
        _avatar.TextAlign = ContentAlignment.MiddleCenter;
        _title.SetName(new PersonName(c.DisplayName, c.Department));
        _unread.SetCount(c.UnreadCount);
        _title.Font = Theme.GetFont(11, c.UnreadCount > 0);
        _preview.ForeColor = c.UnreadCount > 0 ? Theme.Ink : Theme.Muted;
        AccessibleName = new PersonName(c.DisplayName, c.Department) + $", {c.UnreadCount} tin chưa đọc" + (c.UnreadMentionCount > 0 ? ", có nhắc tới bạn" : "") + (c.IsPinned ? ", đã ghim" : "") + (c.IsMuted ? ", im lặng" : "");
        _preview.Text = c.Preview.NormalizeLines(" ");
        _time.Text = c.LastMessageTicks > 0 ? new DateTime(c.LastMessageTicks, DateTimeKind.Utc).ToLocalTime().ToString(
            new DateTime(c.LastMessageTicks, DateTimeKind.Utc).ToLocalTime().Date == DateTime.Today ? "HH:mm" : "dd/MM") : "";
        var indicators = new[] { c.UnreadMentionCount > 0 ? $"@{c.UnreadMentionCount}" : "",
            c.IsPinned ? "◆ Ghim" : "", c.IsMuted ? "Im lặng" : "" }.Where(part => part.Length > 0).ToArray();
        _meta.Text = indicators.Length > 0 ? string.Join(" · ", indicators) :
            c.Kind == ConversationKind.Group ? "Nhóm" : c.Presence == PresenceStatus.Online ? "Trực tuyến" : "Ngoại tuyến";
        FitRow();
        SetActive(active);
    }
}

public sealed class ConversationListControl : UserControl
{
    private readonly PromptTextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Tìm cuộc trò chuyện…", Margin = new Padding(8) };
    private readonly Panel _items = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White };
    private readonly Dictionary<Guid, ConversationItemControl> _rows = [];
    private Guid? _activeKey;
    private ConversationSummary[] _source = [];
    private ConversationSummary[] _visible = [];
    private readonly Button[] _filters;
    private int _filter, _boundFilter = -1;
    private UiPreferences _appearance = new();
    internal void ApplyPreferences(UiPreferences preferences)
    {
        _appearance = preferences;
        var scroll = _items.AutoScrollPosition; _items.SuspendLayout();
        try { foreach(var row in _rows.Values) row.ApplyPreferences(preferences); }
        finally { _items.ResumeLayout(true); }
        _items.AutoScrollPosition = new Point(-scroll.X,-scroll.Y);
    }
    private static Guid Key(ConversationSummary row) => row.Id == Guid.Empty ? row.OtherUserId : row.Id;
    private readonly Label _empty = Theme.Label("Chưa có cuộc trò chuyện.\nMở Người dùng để bắt đầu.");
    public string SearchText => _search.Text.Trim();
    public event EventHandler? SearchChanged;
    public void FocusSearch() { _search.Focus(); _search.SelectAll(); }
    public event Action<ConversationSummary>? Selected;
    public event Action<ConversationSummary, string>? ActionRequested;
    internal void SetActive(Guid? id)
    {
        if (_activeKey == id) return;
        if (_activeKey is Guid previous && _rows.TryGetValue(previous, out var old)) old.SetActive(false);
        _activeKey = id;
        if (id is Guid current && _rows.TryGetValue(current, out var row)) row.SetActive(true);
    }
    public ConversationListControl()
    {
        Dock = DockStyle.Fill; Padding = new Padding(8);
        _search.AccessibleName = "Tìm hội thoại hoặc người dùng";
        _search.PlaceholderText = "Tìm hội thoại, người dùng…";
        _search.TextChanged += (_, _) => SearchChanged?.Invoke(this, EventArgs.Empty);
        _search.KeyDown += (_, e) => { if ((e.KeyCode == Keys.Down || e.KeyCode == Keys.Enter) && _rows.Count > 0) { _items.Controls.OfType<ConversationItemControl>().Last().Focus(); e.SuppressKeyPress = true; } };
        var layout = Theme.Stack(4);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var filters = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 4, 0, 6) };
        filters.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _filters = new[] { "Tất cả", "Chưa đọc", "@ Tôi", "Đã ghim" }.Select((text, index) =>
        {
            var button = new Button { Text = text, AccessibleName = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoEllipsis = true, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
                Font = Theme.GetFont(9, false), Margin = new Padding(1), Padding = new Padding(1, 4, 1, 4), Cursor = Cursors.Hand };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => { _filter = index; Bind(_source, _activeKey); };
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, new[] { 20f, 32f, 20f, 28f }[index])); filters.Controls.Add(button, index, 0);
            return button;
        }).ToArray();
        layout.Controls.Add(Theme.Label("Tin nhắn", 16, true), 0, 0); layout.Controls.Add(_search, 0, 1); layout.Controls.Add(filters, 0, 2);
        layout.Controls.Add(_items, 0, 3); Controls.Add(layout);
        _empty.Dock = DockStyle.Top; _empty.Padding = new Padding(10); _items.Controls.Add(_empty);
    }
    public void Bind(IReadOnlyList<ConversationSummary> conversations, Guid? activeId)
    {
        _source = conversations.ToArray();
        var filtered = _source.Where(c => _filter switch { 1 => c.UnreadCount > 0, 2 => c.UnreadMentionCount > 0, 3 => c.IsPinned, _ => true }).ToArray();
        for (var i = 0; i < _filters.Length; i++)
        {
            _filters[i].BackColor = i == _filter ? Theme.Selection : Color.White;
            _filters[i].ForeColor = i == _filter ? Theme.Blue : Theme.Muted;
            var count = _source.Count(c => i switch { 1 => c.UnreadCount > 0, 2 => c.UnreadMentionCount > 0, 3 => c.IsPinned, _ => true });
            _filters[i].AccessibleName = _filters[i].Text + $", {count} hội thoại";
            _filters[i].AccessibleDescription = i == _filter ? "Bộ lọc đang chọn" : "Lọc danh sách hội thoại";
        }
        if (_boundFilter == _filter && _visible.SequenceEqual(filtered)) { SetActive(activeId); return; }
        _boundFilter = _filter; _visible = filtered; conversations = filtered;
        _items.SuspendLayout();
        foreach (var id in _rows.Keys.Except(conversations.Select(Key)).ToArray()) { _rows[id].Dispose(); _rows.Remove(id); }
        foreach (var c in conversations)
        {
            if (!_rows.TryGetValue(Key(c), out var row))
            {
                row = new ConversationItemControl();
                row.Selected += x => { SetActive(Key(x)); Selected?.Invoke(x); };
                row.ActionRequested += (x, a) => ActionRequested?.Invoke(x, a);
                _rows.Add(Key(c), row);
                Theme.AddAtCurrentDpi(_items, row);
                row.ApplyPreferences(_appearance);
            }
            row.Bind(c, activeId == c.Id);
        }
        // Dock Top lays out in reverse z-order; update order without recreating controls.
        for (var i = 0; i < conversations.Count; i++)
        {
            var row = _rows[Key(conversations[i])]; var index = conversations.Count - i - 1;
            if (_items.Controls.GetChildIndex(row) != index) _items.Controls.SetChildIndex(row, index);
        }
        _empty.Text = _filter != 0 ? "Không có hội thoại trong bộ lọc này." : SearchText.Length == 0 ? "Chưa có cuộc trò chuyện.\nMở Danh bạ để bắt đầu." : "Không tìm thấy kết quả.";
        _empty.Visible = conversations.Count == 0; _items.ResumeLayout();
        _activeKey = activeId;
    }
}
