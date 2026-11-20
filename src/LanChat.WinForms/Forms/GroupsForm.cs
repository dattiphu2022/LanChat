using LanChat.Application;
using LanChat.Domain;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class GroupsForm : DpiForm
{
    private readonly IChatStore _chat;
    private readonly IDistributedStore _store;
    private readonly IConsensusService _consensus;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _stop;
    private readonly SemaphoreSlim _refreshGate = new(1);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly ListBox _groups = new() { Dock = DockStyle.Fill, DisplayMember = nameof(GroupSummary.Name), AllowDrop = true, AccessibleName = "Nhóm chat" };
    private readonly PromptTextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Tìm nhóm…" };
    private readonly GroupMembersControl _members = new(showAllContacts: true);
    private readonly Label _status = Theme.Label("");
    private readonly Label _membersTitle = Theme.Label("Thành viên nhóm", 11, true);
    private readonly Button _newGroup = Theme.Secondary("Nhóm mới", UiIcon.AddPerson);
    private readonly Button _open = Theme.Button("Mở chat", UiIcon.Chat);
    private readonly Button _rename = Theme.Secondary("Đổi tên", UiIcon.Edit);
    private readonly Button _transfer = Theme.Secondary("Chuyển chủ nhóm", UiIcon.SwitchUser);
    private readonly Button _delete = Theme.Secondary("Xóa nhóm", UiIcon.Delete);
    private Dictionary<Guid, GroupSummary> _summaries = [];
    private Dictionary<Guid, UserSummary> _contacts = [];
    private LocalUserProfile? _profile;
    private ConsensusStatus? _network;
    private bool _creating, _saving, _bindingGroups;
    private Guid? _createdGroup;
    private long _confirmedIndex;
    private string? _error;
    private string? _refreshError;

    public Guid? SelectedGroup { get; private set; }
    private Guid? SelectedId => (_groups.SelectedItem as GroupSummary)?.Id;
    private GroupSummary? CurrentGroup => SelectedId is Guid id && _summaries.TryGetValue(id, out var group) ? group : null;
    private bool WaitingForCommit => _confirmedIndex > (_network?.CommitIndex ?? 0);
    private bool CanSubmit => !_saving && !WaitingForCommit && _network?.LeaderReady == true;
    private Guid? PairMember => _network?.PairGroupMemberUserId;
    private bool CanCreateGroup => !_saving && !WaitingForCommit && _profile is not null && (CanSubmit || PairMember.HasValue);
    private bool IsOwner(GroupSummary? group) => _profile is not null && group?.OwnerUserId == _profile.Id;
    private bool CanModify(GroupSummary? group) => CanSubmit && group is { PendingCreation: false } && IsOwner(group);

    public GroupsForm(IChatStore chat, IDistributedStore store, IConsensusService consensus)
    {
        _chat = chat;
        _store = store;
        _consensus = consensus;
        _stop = _lifetime.Token;
        Icon = AppIcon.Value;
        Text = "Quản lý nhóm";
        ClientSize = new Size(850, 610);
        MinimumSize = new Size(620, 460);
        StartPosition = FormStartPosition.CenterParent;

        var layout = Theme.Stack(3);
        layout.Padding = new Padding(12);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        var left = Theme.Stack(2);
        left.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        left.Controls.Add(_search, 0, 0);
        left.Controls.Add(_groups, 0, 1);
        var right = Theme.Stack(2);
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(_membersTitle, 0, 0);
        right.Controls.Add(_members, 0, 1);
        columns.Controls.Add(left, 0, 0);
        columns.Controls.Add(right, 1, 0);

        var actions = Theme.Toolbar();
        foreach (var button in new[] { _newGroup, _open, _rename, _transfer, _delete })
        {
            button.Margin = new Padding(3, 5, 3, 5);
            actions.Controls.Add(button);
        }
        actions.Controls.Add(UiInteraction.CloseButton(this));
        UiInteraction.Wrap(_status, layout);
        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(columns, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        Controls.Add(layout);

        _members.SaveRequested += async changes => await RunAsync(() => changes.ApplyAsync(_store, SubmitAsync, _stop));
        _members.CreateRequested += async members => await CreateGroupAsync(members);
        _newGroup.Click += (_, _) =>
        {
            if (_creating && _members.HasDrafts && !UiInteraction.Discard(this, "Bỏ lựa chọn thành viên nhóm mới để chọn lại?")) return;
            _error = null;
            _creating = true;
            _members.ResetNewGroup();
            BindGroups();
            _members.List.Focus();
        };
        _open.Click += (_, _) =>
        {
            if (CurrentGroup is not { } group) return;
            SelectedGroup = group.Id;
            DialogResult = DialogResult.OK;
            Close();
        };
        _groups.DoubleClick += (_, _) => _open.PerformClick();
        _rename.Click += async (_, _) =>
        {
            if (CurrentGroup is not { } group) return;
            await RunAsync(() =>
            {
                SelectionForm.Prompt(this, "Đổi tên nhóm", group.Name,
                    save: name => SubmitAsync(Command(ControlCommandType.GroupRenamed, group.Id, name: name)));
                return Task.CompletedTask;
            });
        };
        _transfer.Click += async (_, _) =>
        {
            if (CurrentGroup is not { } group) return;
            await RunAsync(async () =>
            {
                using var choose = new SelectionForm("Chọn chủ nhóm mới", group.Members.Where(id => id != _profile!.Id).Select(_members.MemberName));
                if (UiInteraction.ShowDialog(choose, this) == DialogResult.OK && choose.SelectedId is { } id)
                    await SubmitAsync(Command(ControlCommandType.GroupOwnerChanged, group.Id, id));
            });
        };
        _delete.Click += async (_, _) =>
        {
            if (CurrentGroup is not { } group) return;
            await RunAsync(async () =>
            {
                if (MessageBox.Show(UiInteraction.Owner(this), $"Xóa nhóm {group.Name}? Lịch sử vẫn được giữ trong dữ liệu nội bộ.", "Xóa nhóm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    await SubmitAsync(Command(ControlCommandType.GroupDeleted, group.Id));
            });
        };
        _groups.SelectedIndexChanged += (_, _) =>
        {
            if (_bindingGroups) return;
            _creating = false;
            _error = null;
            BindMembers();
        };
        _search.TextChanged += (_, _) => BindGroups();
        WireDragAndDrop();

        Shown += async (_, _) =>
        {
            await RefreshAsync();
            if (!_stop.IsCancellationRequested) _timer.Start();
        };
        _timer.Tick += async (_, _) => { if (!_saving) await RefreshAsync(); };
        FormClosing += (_, e) =>
        {
            if (_saving) { e.Cancel = true; _error = "Đang lưu thay đổi nhóm. Vui lòng chờ hoàn tất."; UpdateActions(); }
            else if (_members.HasDrafts && !UiInteraction.Discard(this, "Bỏ các thay đổi thành viên chưa lưu?")) e.Cancel = true;
        };
        FormClosed += (_, _) => Stop();
        Disposed += (_, _) => Stop();
        UpdateActions();
    }

    private void Stop()
    {
        if (_stop.IsCancellationRequested) return;
        _timer.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task RefreshAsync()
    {
        if (_stop.IsCancellationRequested || !_refreshGate.Wait(0)) return;
        try
        {
            var knownProfile = _profile;
            var snapshot = await Task.Run(async () =>
            {
                var profile = knownProfile ?? await _chat.GetProfileAsync(_stop);
                // Read the commit index before the groups it describes.
                var state = await _consensus.GetStatusAsync(_stop);
                var groups = await _store.GetGroupsAsync(_stop);
                var contacts = await _chat.GetUsersAsync(ct: _stop);
                return (profile, state, groups, contacts);
            }, _stop);
            if (_stop.IsCancellationRequested) return;
            if (_profile is null) _creating = snapshot.groups.Length == 0;
            _profile = snapshot.profile;
            _network = snapshot.state;
            _summaries = snapshot.groups.ToDictionary(group => group.Id);
            _contacts = snapshot.contacts.ToDictionary(person => person.UserId);
            _refreshError = null;
            _members.ReconcileDrafts(snapshot.groups, snapshot.profile.Id);
            var created = _createdGroup is Guid id && _summaries.ContainsKey(id) ? _createdGroup : null;
            if (created.HasValue)
            {
                _createdGroup = null;
                _creating = false;
                _members.ResetNewGroup();
            }
            BindGroups(created);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_stop.IsCancellationRequested) return;
            _refreshError = UiText.HideIpAddresses(ex.Message);
            UpdateActions();
        }
        finally { _refreshGate.Release(); }
    }

    private void BindGroups(Guid? preferred = null)
    {
        var selected = preferred ?? SelectedId;
        var search = _search.Text.Trim();
        var visible = _summaries.Values.Where(g => g.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0).ToArray();
        var index = Array.FindIndex(visible, group => group.Id == selected);
        var selection = _creating || visible.Length == 0 ? -1 : Math.Max(0, index);
        var needsRebuild = !_groups.Items.Cast<GroupSummary>().Select(g => (g.Id, g.Name)).SequenceEqual(visible.Select(g => (g.Id, g.Name)));
        if (needsRebuild || _groups.SelectedIndex != selection)
        {
            _bindingGroups = true;
            _groups.BeginUpdate();
            try
            {
                if (needsRebuild)
                {
                    _groups.Items.Clear();
                    _groups.Items.AddRange(visible);
                }
                _groups.SelectedIndex = selection;
            }
            finally { _groups.EndUpdate(); _bindingGroups = false; }
        }
        BindMembers();
    }

    private void BindMembers()
    {
        var name = _profile is { } profile ? new PersonName(profile.DisplayName, profile.Department) : default;
        _members.Bind(CurrentGroup, _contacts, _profile?.Id ?? Guid.Empty, name, _creating, PairMember);
        UpdateActions();
    }

    private void UpdateActions()
    {
        var group = CurrentGroup;
        var owner = IsOwner(group);
        _groups.Enabled = _search.Enabled = !_saving;
        _newGroup.Enabled = !_saving && !WaitingForCommit && _profile is not null;
        _open.Enabled = !_saving && group is not null;
        var canModify = CanModify(group);
        _rename.Enabled = _delete.Enabled = canModify;
        _transfer.Enabled = canModify && group!.Members.Length > 1;
        var allowMembers = _creating ? CanCreateGroup : canModify;
        _members.SetActions(allowChecks: allowMembers, allowSave: allowMembers);
        _membersTitle.Text = _creating ? "Thành viên nhóm mới" : "Thành viên nhóm";
        var details = group is null ? "Chọn nhóm hoặc tạo nhóm mới." : group.PendingCreation ? "Nhóm mới · đang chờ mạng xác nhận." :
            $"{group.Members.Length} thành viên · {(owner ? "Bạn là chủ nhóm" : "Bạn là thành viên")}";
        _status.Text = (_network is null ? "Đang tải danh sách nhóm…" : (_network.LeaderReady ? "Sẵn sàng cập nhật nhóm." : PairMember.HasValue ? "Có thể tạo nhóm hai người; cập nhật nhóm cũ cần đủ máy điều phối." : "Chưa đủ máy điều phối để xác nhận thay đổi. Tin nhắn vẫn được lưu và đồng bộ khi có kết nối.")) + "\n" +
            (_error ?? _refreshError ?? (_saving ? "Đang xử lý thay đổi nhóm…" : WaitingForCommit ? "Đang đồng bộ thay đổi nhóm…" : details));
    }

    private Task CreateGroupAsync(Guid[] members) => RunAsync(() =>
    {
        var id = AppIdentity.NewId();
        var name = SelectionForm.Prompt(this, "Tên nhóm mới", save: value => SubmitAsync(
            new ControlCommand(AppIdentity.NewId(), ControlCommandType.GroupCreated, _profile!.Id, id, Name: value, Members: members)));
        if (name is null) return Task.CompletedTask;
        _createdGroup = id;
        _search.Clear();
        return Task.CompletedTask;
    }, createGroup: true);

    private ControlCommand Command(ControlCommandType type, Guid group, Guid? target = null, string? name = null) =>
        new(AppIdentity.NewId(), type, _profile!.Id, group, Name: name, TargetUserId: target);

    private async Task SubmitAsync(ControlCommand command)
    {
        var result = await _consensus.ProposeAsync(command, _stop);
        if (!result.Success) throw new InvalidOperationException(result.Error ?? "Chưa xác nhận được thay đổi nhóm.");
        _confirmedIndex = Math.Max(_confirmedIndex, result.Index);
    }

    private async Task RunAsync(Func<Task> action, bool createGroup = false)
    {
        if (!(createGroup ? CanCreateGroup : CanSubmit) || _stop.IsCancellationRequested) return;
        _saving = true;
        _error = null;
        UpdateActions();
        try
        {
            await _refreshGate.WaitAsync(_stop);
            _refreshGate.Release();
            await action();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { _error = UiText.HideIpAddresses(ex.Message); }
        finally
        {
            await RefreshAsync();
            _saving = false;
            if (!_stop.IsCancellationRequested) UpdateActions();
        }
    }

    private void WireDragAndDrop()
    {
        var dragStart = Point.Empty;
        _members.List.MouseDown += (_, e) => dragStart = e.Location;
        _members.List.MouseMove += (_, e) =>
        {
            if (!CanSubmit || e.Button != MouseButtons.Left || Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y) <= 10 ||
                _members.List.SelectedItem is not SelectionItem item || !_contacts.ContainsKey(item.Id)) return;
            _members.List.DoDragDrop(new DataObject("LanChat.UserId", item.Id.ToString()), DragDropEffects.Copy);
        };
        (GroupSummary Group, Guid User)? DragTarget(DragEventArgs e)
        {
            if (!CanSubmit || !Guid.TryParse(e.Data?.GetData("LanChat.UserId")?.ToString(), out var user) ||
                !_contacts.ContainsKey(user)) return null;
            var index = _groups.IndexFromPoint(_groups.PointToClient(new Point(e.X, e.Y)));
            if (index < 0 || _groups.Items[index] is not GroupSummary row ||
                !_summaries.TryGetValue(row.Id, out var group) || !CanModify(group) || group.Members.Contains(user)) return null;
            return (group, user);
        }
        void PreviewDrop(object? sender, DragEventArgs e) => e.Effect = DragTarget(e).HasValue ? DragDropEffects.Copy : DragDropEffects.None;
        _groups.DragEnter += PreviewDrop;
        _groups.DragOver += PreviewDrop;
        _groups.DragDrop += async (_, e) =>
        {
            if (DragTarget(e) is { } target)
                await RunAsync(() => SubmitAsync(Command(ControlCommandType.GroupMemberAdded, target.Group.Id, target.User)));
        };
    }
}
