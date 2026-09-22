using System.Drawing.Imaging;
using LanChat.Application;
using LanChat.Domain;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class GroupsForm : Form
{
    private readonly ListBox _groups = new() { Dock = DockStyle.Fill, DisplayMember = "Name", AllowDrop = true };
    private readonly CheckedListBox _users = new() { Dock = DockStyle.Fill, CheckOnClick = true };
    private readonly Label _status = Theme.Label("");
    private readonly PictureBox _avatar = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly List<Button> _ownerButtons = [];
    private Guid _localUser;
    private bool _loading;
    private readonly Button _create = Theme.Button("Tạo nhóm");
    public Guid? SelectedGroup { get; private set; }
    public GroupsForm(IChatStore chat, IDistributedStore store, IConsensusService consensus)
    {
        Text = "Nhóm chat"; ClientSize = new Size(850, 610); MinimumSize = new Size(780, 540); StartPosition = FormStartPosition.CenterParent;
        var layout = Theme.Stack(3); layout.Padding = new Padding(12); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        var search = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Tìm nhóm…" };
        var left = Theme.Stack(3); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 70)); left.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); left.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); left.Controls.Add(_avatar, 0, 0); left.Controls.Add(search, 0, 1); left.Controls.Add(_groups, 0, 2);
        var right = Theme.Stack(2); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(Theme.Label("Chọn thành viên để tạo/thêm vào nhóm.\nCó thể kéo tên người dùng sang nhóm."), 0, 0); right.Controls.Add(_users, 0, 1);
        columns.Controls.Add(left, 0, 0); columns.Controls.Add(right, 1, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true }; buttons.Controls.Add(_create);
        var open = Theme.Button("Mở chat"); buttons.Controls.Add(open);
        var rename = AddOwner("Đổi tên"); var add = AddOwner("Thêm người"); var remove = AddOwner("Xóa người"); var owner = AddOwner("Chuyển chủ nhóm"); var avatar = AddOwner("Ảnh nhóm"); var delete = AddOwner("Xóa nhóm");
        Button AddOwner(string title) { var b = Theme.Button(title); _ownerButtons.Add(b); buttons.Controls.Add(b); return b; }
        layout.Controls.Add(_status, 0, 0); layout.Controls.Add(columns, 0, 1); layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
        async Task RunAsync(Func<Task> action)
        {
            try { buttons.Enabled = false; await action(); await LoadAsync(); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "Nhóm chat"); }
            finally { if (!IsDisposed) buttons.Enabled = true; }
        }
        async Task SubmitAsync(ControlCommand command)
        {
            var result = await consensus.ProposeAsync(command, _lifetime.Token); if (!result.Success) throw new InvalidOperationException(result.Error);
        }
        ControlCommand Command(ControlCommandType type, GroupSummary g, string? name = null, Guid? target = null, string? image = null) => new(Guid.CreateVersion7(), type, _localUser, g.Id, Name: name, TargetUserId: target, AvatarBase64: image);
        async Task LoadAsync()
        {
            if (_loading || IsDisposed) return; _loading = true;
            try
            {
                _localUser = (await chat.GetIdentityAsync(_lifetime.Token)).UserId;
                var selected = (_groups.SelectedItem as GroupSummary)?.Id;
                var all = (await store.GetGroupsAsync(_lifetime.Token)).Where(x => x.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray(); _groups.Items.Clear(); _groups.Items.AddRange(all); if (selected.HasValue) _groups.SelectedItem = all.FirstOrDefault(x => x.Id == selected); if (_groups.SelectedIndex < 0 && all.Length > 0) _groups.SelectedIndex = 0;
                var known = await chat.GetUsersAsync(ct: _lifetime.Token); var checkedIds = _users.CheckedItems.Cast<SelectionItem>().Select(x => x.Id).ToHashSet();
                _users.Items.Clear(); foreach (var user in known) _users.Items.Add(new SelectionItem(user.UserId, user.DisplayName), checkedIds.Contains(user.UserId));
                await UpdateStatusAsync();
            }
            finally { _loading = false; }
        }
        async Task UpdateStatusAsync()
        {
            var state = await consensus.GetStatusAsync(_lifetime.Token); if (IsDisposed) return;
            var canPropose = state.HasQuorum || state.Role == NodeRole.Follower && state.LeaderNodeId.HasValue && state.LastLeaderHeartbeatTicks > DateTime.UtcNow.AddSeconds(-6).Ticks;
            _status.Text = state.StatusText + "\n" + ((_groups.SelectedItem as GroupSummary) is { } g ? $"{g.Members.Length} thành viên · { (g.OwnerUserId == _localUser ? "Bạn là chủ nhóm" : "Bạn là thành viên")}" : "Chọn nhóm hoặc tạo nhóm mới.");
            _create.Enabled = canPropose; foreach (var b in _ownerButtons) b.Enabled = canPropose && (_groups.SelectedItem as GroupSummary)?.OwnerUserId == _localUser;
        }
        _create.Click += async (_, _) => await RunAsync(async () => { var name = SelectionForm.Prompt(this, "Tên nhóm mới"); if (name is null) return; var members = _users.CheckedItems.Cast<SelectionItem>().Select(x => x.Id).Append(_localUser).Distinct().ToArray(); await SubmitAsync(new(Guid.CreateVersion7(), ControlCommandType.GroupCreated, _localUser, Guid.CreateVersion7(), name, Members: members)); });
        open.Click += (_, _) => { if (_groups.SelectedItem is GroupSummary group) { SelectedGroup = group.Id; DialogResult = DialogResult.OK; Close(); } }; _groups.DoubleClick += (_, _) => open.PerformClick();
        rename.Click += async (_, _) => await RunAsync(async () => { if (_groups.SelectedItem is not GroupSummary g) return; var name = SelectionForm.Prompt(this, "Đổi tên nhóm", g.Name); if (name is not null) await SubmitAsync(Command(ControlCommandType.GroupRenamed, g, name)); });
        add.Click += async (_, _) => await RunAsync(async () => { if (_groups.SelectedItem is not GroupSummary g) return; foreach (var user in _users.CheckedItems.Cast<SelectionItem>().Where(x => !g.Members.Contains(x.Id)).ToArray()) await SubmitAsync(Command(ControlCommandType.GroupMemberAdded, g, target: user.Id)); });
        async Task ChooseMemberAsync(ControlCommandType type)
        {
            if (_groups.SelectedItem is not GroupSummary g) return;
            var known = await chat.GetUsersAsync(ct: _lifetime.Token);
            using var choose = new SelectionForm(type == ControlCommandType.GroupOwnerChanged ? "Chọn chủ nhóm mới" : "Xóa thành viên", g.Members.Where(x => x != _localUser).Select(x => new SelectionItem(x, known.FirstOrDefault(u => u.UserId == x)?.DisplayName ?? x.ToString())));
            if (choose.ShowDialog(this) == DialogResult.OK && choose.SelectedId is { } id) await SubmitAsync(Command(type, g, target: id));
        }
        remove.Click += async (_, _) => await RunAsync(() => ChooseMemberAsync(ControlCommandType.GroupMemberRemoved)); owner.Click += async (_, _) => await RunAsync(() => ChooseMemberAsync(ControlCommandType.GroupOwnerChanged));
        delete.Click += async (_, _) => await RunAsync(async () => { if (_groups.SelectedItem is GroupSummary g && MessageBox.Show(this, $"Xóa nhóm {g.Name}? Lịch sử vẫn được giữ trong dữ liệu nội bộ.", "Xóa nhóm", MessageBoxButtons.YesNo) == DialogResult.Yes) await SubmitAsync(Command(ControlCommandType.GroupDeleted, g)); });
        avatar.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_groups.SelectedItem is not GroupSummary g) return; using var file = new OpenFileDialog { Filter = "Ảnh|*.jpg;*.jpeg;*.png;*.bmp" }; if (file.ShowDialog(this) != DialogResult.OK) return;
            using var source = Image.FromFile(file.FileName); using var small = new Bitmap(source, new Size(128, 128)); using var bytes = new MemoryStream(); small.Save(bytes, ImageFormat.Jpeg);
            await SubmitAsync(Command(ControlCommandType.GroupAvatarChanged, g, image: Convert.ToBase64String(bytes.ToArray())));
        });
        _groups.SelectedIndexChanged += async (_, _) =>
        {
            _avatar.Image?.Dispose(); _avatar.Image = null;
            try { if ((_groups.SelectedItem as GroupSummary)?.AvatarBase64 is { } data) { using var bytes = new MemoryStream(Convert.FromBase64String(data)); using var img = Image.FromStream(bytes); _avatar.Image = new Bitmap(img); } await UpdateStatusAsync(); }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OperationCanceledException) { }
        };
        Point dragStart = Point.Empty;
        _users.MouseDown += (_, e) => dragStart = e.Location;
        _users.MouseMove += (_, e) => { if (e.Button == MouseButtons.Left && Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y) > 10 && _users.SelectedItem is SelectionItem item) { var data = new DataObject("LanChat.UserId", item.Id.ToString()); _users.DoDragDrop(data, DragDropEffects.Copy); } };
        _groups.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent("LanChat.UserId") == true ? DragDropEffects.Copy : DragDropEffects.None;
        _groups.DragDrop += async (_, e) => await RunAsync(async () => { var index = _groups.IndexFromPoint(_groups.PointToClient(new Point(e.X, e.Y))); if (index >= 0 && _groups.Items[index] is GroupSummary g && Guid.TryParse(e.Data?.GetData("LanChat.UserId")?.ToString(), out var id)) await SubmitAsync(Command(ControlCommandType.GroupMemberAdded, g, target: id)); });
        search.TextChanged += async (_, _) => await RunAsync(LoadAsync);
        Shown += async (_, _) => { await RunAsync(LoadAsync); _timer.Start(); }; _timer.Tick += async (_, _) => { try { await UpdateStatusAsync(); } catch (Exception ex) { if (!IsDisposed) _status.Text = ex.Message; } };
        FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); _lifetime.Cancel(); _avatar.Image?.Dispose(); };
    }
}
