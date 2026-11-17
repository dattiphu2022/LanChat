using LanChat.Application;
using LanChat.Domain;

namespace LanChat.WinForms.Controls;

internal sealed class GroupMembersControl : UserControl
{
    private readonly PersonNameCheckedListBox _list = new(item => ((SelectionItem)item).DisplayName)
        { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, AccessibleName = "Thành viên nhóm" };
    private readonly Label _hint = Theme.Label("");
    private readonly IconButton _save = Theme.Button("Lưu thành viên", UiIcon.Save);
    private readonly Dictionary<Guid, Dictionary<Guid, bool>> _drafts = [];
    private readonly HashSet<Guid> _newGroupMembers = [];
    private readonly bool _showAllContacts;
    private IReadOnlyDictionary<Guid, UserSummary> _contacts = new Dictionary<Guid, UserSummary>();
    private GroupSummary? _group;
    private HashSet<Guid> _members = [];
    private Guid _localUser;
    private Guid? _pairMember;
    private PersonName _localName;
    private bool _creating, _binding, _allowChecks, _allowSave;
    private string? _status;

    public CheckedListBox List => _list;
    public event Action<GroupMemberChanges>? SaveRequested;
    public event Action<Guid[]>? CreateRequested;

    private bool IsOwner => _group?.OwnerUserId == _localUser;
    private bool CanEdit => _creating || (IsOwner && _group is { PendingCreation: false });

    public GroupMembersControl(bool showAllContacts = false)
    {
        _showAllContacts = showAllContacts;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;

        var layout = Theme.Stack(3);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var actions = Theme.Toolbar();
        actions.FlowDirection = FlowDirection.RightToLeft;
        actions.Controls.Add(_save);
        layout.Controls.Add(_hint, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);

        _list.IsCheckEnabled = item => CanCheck((SelectionItem)item);
        _list.ItemCheck += MemberChecked;
        _save.Click += (_, _) =>
        {
            if (!_save.Enabled) return;
            if (_creating)
                CreateRequested?.Invoke(_pairMember is Guid peer ? [_localUser, peer] : _newGroupMembers.Append(_localUser).ToArray());
            else if (IsOwner && _group is { } group && _drafts.TryGetValue(group.Id, out var edits))
                SaveRequested?.Invoke(new GroupMemberChanges(group.Id, _localUser, edits.ToArray()));
        };
        UpdateActions();
    }

    public void ResetNewGroup() => _newGroupMembers.Clear();

    public void ReconcileDrafts(IEnumerable<GroupSummary> groups, Guid localUser)
    {
        if (_drafts.Count == 0) return;
        var owned = groups.Where(group => group.OwnerUserId == localUser).ToDictionary(group => group.Id);
        foreach (var draft in _drafts.ToArray())
        {
            if (owned.TryGetValue(draft.Key, out var group))
            {
                var members = new HashSet<Guid>(group.Members);
                foreach (var id in draft.Value.Where(edit => members.Contains(edit.Key) == edit.Value).Select(edit => edit.Key).ToArray())
                    draft.Value.Remove(id);
                if (draft.Value.Count > 0) continue;
            }
            _drafts.Remove(draft.Key);
        }
    }

    public void Bind(GroupSummary? group, IReadOnlyDictionary<Guid, UserSummary> contacts,
        Guid localUser, PersonName localName, bool creating = false, Guid? pairMember = null)
    {
        var resetViewport = _group?.Id != group?.Id || _creating != creating;
        _group = group;
        _contacts = contacts;
        _localUser = localUser;
        _localName = localName;
        _creating = creating;
        _pairMember = creating ? pairMember : null;
        _members = new HashSet<Guid>(group?.Members ?? []);
        var edits = IsOwner && group is not null && _drafts.TryGetValue(group.Id, out var changes) ? changes : null;

        SelectionItem[] rows = [];
        if (localUser != Guid.Empty && (creating || group is not null))
        {
            var owner = creating ? localUser : group?.OwnerUserId;
            var ids = _pairMember is Guid peer ? new[] { localUser, peer } :
                _showAllContacts || CanEdit ? contacts.Keys.Append(localUser).Concat(_members).Distinct() : _members;
            rows = ids.Select(MemberName).OrderByDescending(person => person.Id == owner)
                .ThenByDescending(person => _members.Contains(person.Id))
                .ThenBy(person => person.ToString(), StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        BindRows(rows, edits, resetViewport);
    }

    public SelectionItem MemberName(Guid id)
    {
        var name = id == _localUser ? new PersonName(_localName.Name + " · Bạn", _localName.Department) :
            _contacts.TryGetValue(id, out var contact) ? new PersonName(contact.DisplayName, contact.Department) :
            new PersonName($"Chưa có hồ sơ · {id.ToString("N").Substring(0, 8)}");
        if (id == _group?.OwnerUserId) name = new PersonName(name.Name + " · Chủ nhóm", name.Department);
        return new SelectionItem(id, name);
    }

    public void SetActions(bool allowChecks, bool allowSave, string? status = null)
    {
        _allowChecks = allowChecks;
        _allowSave = allowSave;
        _status = status;
        UpdateActions();
    }

    private bool CanCheck(SelectionItem person) => _allowChecks && CanEdit && !_pairMember.HasValue && person.Id != _localUser;

    private void BindRows(SelectionItem[] rows, Dictionary<Guid, bool>? edits, bool resetViewport)
    {
        var selected = resetViewport ? null : (_list.SelectedItem as SelectionItem)?.Id;
        var top = resetViewport ? 0 : _list.TopIndex;
        _binding = true;
        _list.BeginUpdate();
        try
        {
            var rebuild = !_list.Items.Cast<SelectionItem>().SequenceEqual(rows);
            if (rebuild)
            {
                _list.Items.Clear();
                _list.Items.AddRange(rows);
            }
            if (rebuild || resetViewport)
            {
                _list.SelectedIndex = Array.FindIndex(rows, row => row.Id == selected);
                if (rows.Length > 0) _list.TopIndex = Math.Min(Math.Max(0, top), rows.Length - 1);
            }
            for (var i = 0; i < rows.Length; i++)
            {
                var id = rows[i].Id;
                var check = _creating ? id == _localUser || (_pairMember is Guid peer ? id == peer : _newGroupMembers.Contains(id)) :
                    edits is not null && edits.TryGetValue(id, out var draft) ? draft : _members.Contains(id);
                if (_list.GetItemChecked(i) != check) _list.SetItemChecked(i, check);
            }
        }
        finally { _list.EndUpdate(); _binding = false; }
        UpdateActions();
    }

    private void MemberChecked(object? sender, ItemCheckEventArgs e)
    {
        if (_binding) return;
        var person = (SelectionItem)_list.Items[e.Index];
        if (!CanCheck(person))
        {
            e.NewValue = e.CurrentValue;
            return;
        }

        var check = e.NewValue == CheckState.Checked;
        if (_creating)
        {
            if (check) _newGroupMembers.Add(person.Id);
            else _newGroupMembers.Remove(person.Id);
        }
        else if (_group is { } group)
        {
            if (!_drafts.TryGetValue(group.Id, out var edits)) _drafts[group.Id] = edits = [];
            if (_members.Contains(person.Id) == check) edits.Remove(person.Id);
            else edits[person.Id] = check;
        }
        UpdateActions();
    }

    private void UpdateActions()
    {
        _save.Visible = CanEdit;
        _save.Enabled = _allowSave && CanEdit && (_creating ||
            (_group is { } group && _drafts.TryGetValue(group.Id, out var edits) && edits.Count > 0));
        _save.BackColor = _save.Enabled ? Theme.Blue : Theme.Border;
        _save.Text = _creating ? "Tạo nhóm" : "Lưu thành viên";
        _save.Icon = _creating ? UiIcon.AddPerson : UiIcon.Save;
        _hint.Text = _status ?? (_creating ? (_pairMember.HasValue ? "Nhóm sẽ gồm hai người dùng đang kết nối." : "Tích chọn người rồi bấm Tạo nhóm.") :
            _group is null ? "Chọn một nhóm để xem thành viên." :
            _group.PendingCreation ? "Chờ mạng xác nhận để sửa thành viên." :
            IsOwner ? "Tích để thêm, bỏ tích để bớt." : "Chỉ chủ nhóm có thể thay đổi thành viên.");
        _list.Invalidate();
    }
}

internal sealed record GroupMemberChanges(Guid GroupId, Guid ActorUserId, KeyValuePair<Guid, bool>[] Edits)
{
    public async Task ApplyAsync(IDistributedStore store, Func<ControlCommand, Task> submit, CancellationToken ct)
    {
        var group = (await store.GetGroupsAsync(ct)).FirstOrDefault(group => group.Id == GroupId);
        if (group is null || group.OwnerUserId != ActorUserId)
            throw new InvalidOperationException("Chỉ chủ nhóm được thay đổi thành viên.");

        var members = new HashSet<Guid>(group.Members);
        var added = Edits.Where(edit => edit.Value && !members.Contains(edit.Key)).Select(edit => edit.Key).ToArray();
        var removed = Edits.Where(edit => !edit.Value && edit.Key != ActorUserId && members.Contains(edit.Key)).Select(edit => edit.Key).ToArray();
        if (members.Count - removed.Length + added.Length > 100)
            throw new InvalidOperationException("Nhóm tối đa 100 thành viên.");

        ControlCommand Command(ControlCommandType type, Guid user) =>
            new(AppIdentity.NewId(), type, ActorUserId, GroupId, TargetUserId: user);
        foreach (var user in removed) await submit(Command(ControlCommandType.GroupMemberRemoved, user));
        foreach (var user in added) await submit(Command(ControlCommandType.GroupMemberAdded, user));
    }
}
