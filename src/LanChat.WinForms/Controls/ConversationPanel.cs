using LanChat.Application;
using LanChat.Infrastructure;

namespace LanChat.WinForms.Controls;

internal sealed class ConversationPanel : UserControl
{
    private readonly PromptTextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Tìm trong hội thoại…" };
    private readonly SearchResultListBox _results = new() { Dock = DockStyle.Fill, AccessibleName = "Kết quả trong hội thoại hiện tại",
        Describe = item => item is SearchRow row ? ($"Hội thoại hiện tại · {new DateTime(row.Item.CreatedAtTicks, DateTimeKind.Utc).ToLocalTime():dd/MM HH:mm}", row.Item.Text) : ("",item.ToString() ?? "") };
    private readonly Button _stopSearch = Theme.Secondary("Dừng", UiIcon.Stop);
    public event Action? GlobalSearchRequested;
    private readonly Button _find = Theme.Button("Tìm", UiIcon.Search);
    private readonly Label _searchStatus = Theme.Label("");
    private readonly ListBox _images = List("Ảnh");
    private readonly ListBox _files = List("Tệp");
    private readonly GroupMembersControl _members = new();
    private readonly IconTabControl _tabs = new(UiIcon.Photo, UiIcon.File, UiIcon.Search, UiIcon.People)
        { Dock = DockStyle.Fill, Multiline = true, Padding = new Point(5, 3) };
    private readonly TabPage _searchTab;
    private readonly TabPage _membersTab;
    private AttachmentDto[] _attachments = [];
    private AttachmentActions? _attachmentActions;
    private readonly Dictionary<ListBox, Action> _refreshAttachmentActions = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ToolTip _locationTips = new() { AutoPopDelay = 15000 };
    private bool _stopped;
    internal bool HasMemberDrafts => _members.HasDrafts;
    private static readonly Dictionary<Guid, UserSummary> NoContacts = [];

    public event Action? CloseRequested;
    public event Action<string>? SearchRequested;
    public event Action? SearchChanged;
    public event Action<SearchResult>? ResultSelected;
    public event Action<AttachmentDto>? FileRequested;
    internal event Action<AttachmentDto, AttachmentCommand>? AttachmentCommandRequested;
    public event Action<GroupMemberChanges>? MemberSaveRequested;

    public ConversationPanel()
    {
        _searchTab = _tabs.CreatePage("Tìm tin", UiIcon.Search);
        _membersTab = _tabs.CreatePage("Thành viên", UiIcon.People);
        Dock = DockStyle.Fill; Visible = false; BackColor = Theme.Surface; Padding = new Padding(6, 0, 0, 0);
        var root = Theme.Stack(2);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 49)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(Theme.Label("Thông tin hội thoại", 11, true), 0, 0);
        var close = Theme.Icon(UiIcon.Close, "Đóng thông tin hội thoại"); close.MinimumSize = new Size(28, 32);
        close.Click += (_, _) => CloseRequested?.Invoke(); header.Controls.Add(close, 1, 0);
        root.Controls.Add(header, 0, 0); root.Controls.Add(_tabs, 0, 1); Controls.Add(root);

        var searchLayout = Theme.Stack(4); searchLayout.Padding = new Padding(5);
        searchLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); searchLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        searchLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        searchLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var searchActions = Theme.Toolbar();
        var globalSearch = Theme.Secondary("Toàn bộ lịch sử…", UiIcon.Search);
        globalSearch.Click += (_, _) => GlobalSearchRequested?.Invoke();
        _stopSearch.Enabled = false; _stopSearch.Click += (_, _) => { SearchChanged?.Invoke(); _searchStatus.Text = "Đã dừng tìm kiếm."; };
        searchActions.Controls.AddRange([_find, _stopSearch, globalSearch]);
        UiInteraction.Wrap(_searchStatus, searchLayout);
        _search.AccessibleName = "Tìm tin nhắn trong hội thoại";
        _search.TextChanged += (_, _) =>
        {
            SearchChanged?.Invoke(); _results.Items.Clear(); _results.Enabled = true;
            _searchStatus.Text = ""; _find.Enabled = _search.Text.Trim().Length > 0;
        };
        void Search() { var query = _search.Text.Trim(); if (_find.Enabled && query.Length > 0) SearchRequested?.Invoke(query); }
        _find.Enabled = false;
        _find.Click += (_, _) => Search();
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(); } };
        searchLayout.Controls.Add(_search, 0, 0); searchLayout.Controls.Add(searchActions, 0, 1); searchLayout.Controls.Add(_results, 0, 2);
        searchLayout.Controls.Add(_searchStatus, 0, 3);
        _searchTab.Controls.Add(searchLayout);
        var photos = _tabs.CreatePage("Ảnh", UiIcon.Photo); photos.Controls.Add(AttachmentList(_images));
        var files = _tabs.CreatePage("Tệp", UiIcon.File); files.Controls.Add(AttachmentList(_files));
        _membersTab.Controls.Add(_members);
        _members.SaveRequested += changes => MemberSaveRequested?.Invoke(changes);
        _tabs.TabPages.AddRange([photos, files, _searchTab]);
        Wire(_results, () => { if (_results.SelectedItem is SearchRow row) ResultSelected?.Invoke(row.Item); });
        Wire(_images, () => Open(_images)); Wire(_files, () => Open(_files));
    }

    private Control AttachmentList(ListBox list)
    {
        var layout = Theme.Stack(3);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var location = Theme.Label("", 8); location.AutoSize = false; location.AutoEllipsis = true; location.Height = 24; location.ForeColor = Theme.Muted;
        var buttons = Theme.Toolbar(); var open = Theme.Secondary("Mở", UiIcon.Open); var save = Theme.Secondary("Lưu về máy", UiIcon.Save);
        var more = Theme.Icon(UiIcon.More, "Lưu các đính kèm của tin nhắn");
        var menu = new ThemeMenuStrip();
        var all = menu.Items.Add("Lưu tất cả đính kèm của tin…", UiIcons.ForMenu(UiIcon.Save));
        void Command(AttachmentCommand command) { if (list.SelectedItem is FileRow row) AttachmentCommandRequested?.Invoke(row.Item, command); }
        open.Click += (_, _) => Command(AttachmentCommand.Open); save.Click += (_, _) => Command(AttachmentCommand.Save);
        more.Click += (_, _) => menu.Show(more, new Point(0, more.Height)); all.Click += (_, _) => Command(AttachmentCommand.SaveMessage);
        var generation = 0; Guid? watched = null;
        layout.Disposed += (_, _) => { if (watched is Guid id) _attachmentActions?.Unwatch(id); };
        async void Update()
        {
            if (_stopped) return;
            var request = ++generation;
            var file = (list.SelectedItem as FileRow)?.Item;
            if (watched != file?.Id)
            {
                if (watched is Guid previous) _attachmentActions?.Unwatch(previous);
                watched = file?.Id; if (watched is Guid next) _attachmentActions?.Watch(next);
            }
            open.Enabled = save.Enabled = more.Enabled = file is not null && _attachmentActions?.IsBusy(file.Id) != true;
            location.Text = "";
            if (file is null || _attachmentActions is null || IsDisposed) return;
            if (!open.Enabled) { location.Text = _attachmentActions.State(file.Id); return; }
            if (_attachmentActions.BackgroundState(file.Id) is { } state) { location.Text = state; return; }
            try
            {
                var local = await _attachmentActions.LocationAsync(file, _lifetime.Token);
                if (IsDisposed || request != generation) return;
                location.Text = local.UserSelected ? (local.Exists ? "Đã lưu: " : "Không tìm thấy: ") + local.Path : "Bản trên máy này";
                _locationTips.SetToolTip(location, local.Path); _locationTips.SetToolTip(open, "Mở: " + local.Path);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed && request == generation) { location.Text = "Chưa đọc được vị trí"; _locationTips.SetToolTip(location, ex.Message); } }
        }
        _refreshAttachmentActions.Add(list, Update);
        list.SelectedIndexChanged += (_, _) => Update(); Update();
        buttons.Controls.AddRange([open, save, more]); layout.Controls.Add(list, 0, 0); layout.Controls.Add(buttons, 0, 1); layout.Controls.Add(location, 0, 2);
        layout.Disposed += (_, _) => menu.Dispose(); return layout;
    }

    internal void ConfigureAttachments(AttachmentActions actions)
    {
        if (_attachmentActions is not null) _attachmentActions.Changed -= AttachmentChanged;
        _attachmentActions = actions; actions.Changed += AttachmentChanged;
    }
    private void AttachmentChanged(Guid id)
    {
        foreach (var item in _refreshAttachmentActions)
            if (item.Key.SelectedItem is FileRow row && row.Item.Id == id) item.Value();
    }

    public void Bind(AttachmentDto[] attachments, GroupSummary[]? groups, Guid conversationId,
        IReadOnlyList<UserSummary> contacts, Guid localUser, PersonName localName)
    {
        BindFiles(attachments);
        var group = groups?.FirstOrDefault(item => item.Id == conversationId);
        if (groups is not null) _members.ReconcileDrafts(groups, localUser);
        _members.Bind(group, group is null ? NoContacts : contacts.ToDictionary(person => person.UserId), localUser, localName);
        ShowMembersTab(group is not null);
    }

    private void BindFiles(AttachmentDto[] attachments)
    {
        var ordered = attachments.OrderByDescending(f => f.Id).ToArray();
        if (!_attachments.SequenceEqual(ordered))
        {
            _attachments = ordered;
            SetFiles(_images, ordered.Where(ChatImageService.IsImage));
            SetFiles(_files, ordered.Where(f => !ChatImageService.IsImage(f)));
        }
    }

    private void ShowMembersTab(bool visible)
    {
        var shown = _tabs.TabPages.Contains(_membersTab);
        if (visible && !shown) _tabs.TabPages.Add(_membersTab);
        if (!visible && shown) _tabs.TabPages.Remove(_membersTab);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_stopped)
        {
            _stopped = true; _lifetime.Cancel(); _lifetime.Dispose(); _locationTips.Dispose();
            if (_attachmentActions is not null) _attachmentActions.Changed -= AttachmentChanged;
        }
        if (disposing && _membersTab.Parent is null) _membersTab.Dispose();
        base.Dispose(disposing);
    }

    public void ResetConversation(bool isGroup)
    {
        _search.Clear();
        _results.Items.Clear();
        BindFiles([]);
        _members.Bind(null, NoContacts, Guid.Empty, default);
        _members.SetActions(false, false, isGroup ? "Đang tải thành viên…" : null);
        ShowMembersTab(isGroup);
    }

    public void SetMemberActions(bool allowChecks, bool allowSave, string? status) =>
        _members.SetActions(allowChecks, allowSave, status);

    public void FocusSearch() { _tabs.SelectedTab = _searchTab; _search.Focus(); _search.SelectAll(); }
    public void SetResults(string query, SearchResult[] rows)
    {
        if (_search.Text.Trim() != query) return;
        _results.Query = query;
        _results.Items.Clear();
        _results.Items.AddRange(rows.Select(x => new SearchRow(x)).ToArray());
        SetSearchState(query, false, rows.Length == 0 ? "Không tìm thấy tin nhắn." : $"Tìm thấy {rows.Length} tin nhắn.");
    }

    public void SetSearchState(string query, bool working, string? message = null)
    {
        if (_search.Text.Trim() != query) return;
        _find.Enabled = !working && query.Length > 0; _stopSearch.Enabled = working; _results.Enabled = !working;
        _searchStatus.Text = message ?? (working ? "Đang tìm tin nhắn…" : "");
        if (working) _results.Items.Clear();
    }

    public void StopSearch() => SetSearchState(_search.Text.Trim(), false);

    private void Open(ListBox list) { if (list.SelectedItem is FileRow row) FileRequested?.Invoke(row.Item); }
    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter)
        {
            if (_results.ContainsFocus) { if (_results.SelectedItem is SearchRow row) ResultSelected?.Invoke(row.Item); return true; }
            if (_images.ContainsFocus) { Open(_images); return true; }
            if (_files.ContainsFocus) { Open(_files); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private static void SetFiles(ListBox list, IEnumerable<AttachmentDto> files)
    {
        var selected = (list.SelectedItem as FileRow)?.Item.Id;
        list.BeginUpdate(); list.Items.Clear();
        foreach (var file in files)
        {
            var index = list.Items.Add(new FileRow(file));
            if (file.Id == selected) list.SelectedIndex = index;
        }
        if (list.Items.Count == 0) list.Items.Add("Chưa có nội dung.");
        list.EndUpdate();
    }
    private static ListBox List(string name) => new() { Dock = DockStyle.Fill, AccessibleName = name, BorderStyle = BorderStyle.None, IntegralHeight = false, HorizontalScrollbar = true };
    private static void Wire(ListBox list, Action action)
    {
        list.DoubleClick += (_, _) => action();
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; action(); } };
    }
    private sealed record SearchRow(SearchResult Item)
    {
        public override string ToString() => $"{new DateTime(Item.CreatedAtTicks, DateTimeKind.Utc).ToLocalTime():dd/MM HH:mm}   {Item.Text.Replace('\r', ' ').Replace('\n', ' ')}";
    }
    private sealed record FileRow(AttachmentDto Item)
    {
        public override string ToString() => Item.MimeType == FolderAttachment.MimeType
            ? $"{Item.FileName}  ·  Thư mục" : $"{Item.FileName}  ·  {Item.FileSize / 1024d:N1} KB";
    }
}
