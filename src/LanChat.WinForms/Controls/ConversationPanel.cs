using LanChat.Application;
using LanChat.Infrastructure;

namespace LanChat.WinForms.Controls;

internal sealed class ConversationPanel : UserControl
{
    private readonly PromptTextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Tìm trong hội thoại…" };
    private readonly ListBox _results = List("Kết quả tìm tin");
    private readonly ListBox _images = List("Ảnh");
    private readonly ListBox _files = List("Tệp");
    private readonly GroupMembersControl _members = new();
    private readonly IconTabControl _tabs = new(UiIcon.Photo, UiIcon.File, UiIcon.Search, UiIcon.People)
        { Dock = DockStyle.Fill, Multiline = true, Padding = new Point(5, 3) };
    private readonly TabPage _searchTab;
    private readonly TabPage _membersTab;
    private AttachmentDto[] _attachments = [];
    private static readonly Dictionary<Guid, UserSummary> NoContacts = [];

    public event Action? CloseRequested;
    public event Action<string>? SearchRequested;
    public event Action<SearchResult>? ResultSelected;
    public event Action<AttachmentDto>? FileRequested;
    public event Action<GroupMemberChanges>? MemberSaveRequested;

    public ConversationPanel()
    {
        _searchTab = _tabs.CreatePage("Tìm tin", UiIcon.Search);
        _membersTab = _tabs.CreatePage("Thành viên", UiIcon.People);
        Dock = DockStyle.Fill; Visible = false; BackColor = Color.White; Padding = new Padding(6, 0, 0, 0);
        var root = Theme.Stack(2);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 49)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(Theme.Label("Thông tin hội thoại", 11, true), 0, 0);
        var close = Theme.Icon(UiIcon.Close, "Đóng thông tin hội thoại"); close.MinimumSize = new Size(28, 32);
        close.Click += (_, _) => CloseRequested?.Invoke(); header.Controls.Add(close, 1, 0);
        root.Controls.Add(header, 0, 0); root.Controls.Add(_tabs, 0, 1); Controls.Add(root);

        var searchLayout = Theme.Stack(3); searchLayout.Padding = new Padding(5);
        searchLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); searchLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        searchLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var find = Theme.Button("Tìm", UiIcon.Search); find.Dock = DockStyle.Left;
        _search.AccessibleName = "Tìm tin nhắn trong hội thoại";
        _search.TextChanged += (_, _) => { _results.Items.Clear(); find.Enabled = _search.Text.Trim().Length > 0; };
        void Search() { var query = _search.Text.Trim(); if (query.Length > 0) SearchRequested?.Invoke(query); }
        find.Enabled = false;
        find.Click += (_, _) => Search();
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(); } };
        searchLayout.Controls.Add(_search, 0, 0); searchLayout.Controls.Add(find, 0, 1); searchLayout.Controls.Add(_results, 0, 2);
        _searchTab.Controls.Add(searchLayout);
        var photos = _tabs.CreatePage("Ảnh", UiIcon.Photo); photos.Controls.Add(_images);
        var files = _tabs.CreatePage("Tệp", UiIcon.File); files.Controls.Add(_files);
        _membersTab.Controls.Add(_members);
        _members.SaveRequested += changes => MemberSaveRequested?.Invoke(changes);
        _tabs.TabPages.AddRange([photos, files, _searchTab]);
        Wire(_results, () => { if (_results.SelectedItem is SearchRow row) ResultSelected?.Invoke(row.Item); });
        Wire(_images, () => Open(_images)); Wire(_files, () => Open(_files));
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
        _results.Items.Clear();
        if (rows.Length == 0) _results.Items.Add("Không tìm thấy tin nhắn.");
        else _results.Items.AddRange(rows.Select(x => new SearchRow(x)).ToArray());
    }

    private void Open(ListBox list) { if (list.SelectedItem is FileRow row) FileRequested?.Invoke(row.Item); }
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
