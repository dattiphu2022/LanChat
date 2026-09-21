using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace LanChat.WinForms.Forms;

public sealed partial class MainForm : Form
{
    private readonly IChatStore _store;
    private readonly IChatService _chatService;
    private readonly ILanScanner _scanner;
    private readonly IServiceProvider _services;
    private readonly NetworkHealth _health;
    private readonly ConversationListControl _conversations = new();
    private readonly ChatViewControl _chat = new();
    private readonly Panel _people = new() { Dock = DockStyle.Fill, AutoScroll = true, Visible = false, BackColor = Color.White };
    private readonly TextBox _userSearch = new() { Dock = DockStyle.Top, PlaceholderText = "Tìm người dùng…" };
    private readonly Panel _userRows = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Dictionary<Guid, UserItemControl> _users = [];
    private readonly Label _profile = Theme.Label("LanChat", 13, true);
    private readonly PictureBox _avatar = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1200 };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _scanCancellation;
    private bool _refreshing;
    private Guid _localUser;
    private Guid? _activeConversation;
    private Guid? _recipient;
    private int _messageLimit = 200;
    private readonly NavigationItemControl _messageNav = new() { Caption = "✉  Tin nhắn", Active = true };
    private readonly NavigationItemControl _usersNav = new() { Caption = "●  Người dùng" };
    public MainForm(IChatStore store, IChatService chatService, ILanScanner scanner, IServiceProvider services, NetworkHealth health)
    {
        _store = store; _chatService = chatService; _scanner = scanner; _services = services; _health = health;
        Text = "LanChat — Chat nội bộ"; ClientSize = new Size(1240, 800); MinimumSize = new Size(1020, 650);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = Theme.Canvas;
        var root = Theme.Stack(3); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Padding = new Padding(12, 6, 12, 6) };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        toolbar.Controls.Add(_avatar, 0, 0); toolbar.Controls.Add(_profile, 1, 0);
        var scan = Theme.Button("Quét lại mạng"); toolbar.Controls.Add(scan, 2, 0); var diagnostics = Theme.Button("Chẩn đoán"); toolbar.Controls.Add(diagnostics, 3, 0);
        root.Controls.Add(toolbar, 0, 0);
        var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155)); workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.White };
        foreach (var item in new[] { _messageNav, _usersNav }) { item.Width = 150; navigation.Controls.Add(item); }
        foreach (var title in new[] { "♧  Nhóm", "▤  File" })
        {
            var item = new NavigationItemControl { Caption = title, Width = 150 };
            item.Caption += " · Sắp có"; item.Enabled = false; navigation.Controls.Add(item);
        }
        var settings = new NavigationItemControl { Caption = "⚙  Cài đặt", Width = 150 }; navigation.Controls.Add(settings);
        workspace.Controls.Add(navigation, 0, 0);
        var split = new SplitContainer();
        ((System.ComponentModel.ISupportInitialize)split).BeginInit();
        split.Dock = DockStyle.Fill; split.Size = new Size(1050, 680); split.SplitterDistance = 320;
        split.Panel1MinSize = 260; split.Panel2MinSize = 420; split.BackColor = Color.FromArgb(223, 228, 237);
        split.Panel1.Controls.Add(_conversations); split.Panel1.Controls.Add(_people); split.Panel2.Controls.Add(_chat);
        var peopleLayout = Theme.Stack(3); peopleLayout.Padding = new Padding(10);
        peopleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); peopleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); peopleLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        peopleLayout.Controls.Add(Theme.Label("Người dùng", 16, true), 0, 0); peopleLayout.Controls.Add(_userSearch, 0, 1); peopleLayout.Controls.Add(_userRows, 0, 2); _people.Controls.Add(peopleLayout);
        workspace.Controls.Add(split, 1, 0); root.Controls.Add(workspace, 0, 1);
        ((System.ComponentModel.ISupportInitialize)split).EndInit();
        split.Panel1.ClientSizeChanged += (_, _) => { _conversations.Size = split.Panel1.ClientSize; _people.Size = split.Panel1.ClientSize; };
        split.Panel2.ClientSizeChanged += (_, _) => _chat.Size = split.Panel2.ClientSize;
        var status = new StatusStrip { Dock = DockStyle.Fill }; status.Items.Add(_status); root.Controls.Add(status, 0, 2); Controls.Add(root);
        _messageNav.Selected += (_, _) => ShowPeople(false); _usersNav.Selected += (_, _) => ShowPeople(true);
        _conversations.Selected += async c => await RunUiAsync(() => SelectConversationAsync(c.Id, c.OtherUserId));
        _conversations.ActionRequested += async (c, action) => await RunUiAsync(async () =>
        {
            await store.SetConversationAsync(c.Id, action == "pin" ? !c.IsPinned : null, action == "mute" ? !c.IsMuted : null, action == "read", _lifetime.Token);
            await RefreshAsync();
        });
        _chat.SendRequested += async text =>
        {
            if (_recipient is null) return;
            await chatService.SendAsync(_recipient.Value, text, _lifetime.Token); await RefreshAsync();
        };
        _chat.OlderRequested += async () => await RunUiAsync(async () => { _messageLimit += 200; await RefreshAsync(); });
        scan.Click += async (_, _) => await ScanAsync(scan);
        settings.Selected += async (_, _) => { using var form = ActivatorUtilities.CreateInstance<SettingsForm>(services); form.ShowDialog(this); await RunUiAsync(UpdateProfileAsync); };
        diagnostics.Click += (_, _) => { using var form = ActivatorUtilities.CreateInstance<ClusterDiagnosticsForm>(services); form.ShowDialog(this); };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => await RunUiAsync(async () => { await UpdateProfileAsync(); await RefreshAsync(); _timer.Start(); });
        FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); _lifetime.Cancel(); _scanCancellation?.Cancel(); _avatar.Image?.Dispose(); };
    }
    private void ShowPeople(bool value) { _people.Visible = value; _conversations.Visible = !value; _usersNav.Active = value; _messageNav.Active = !value; }
    private async Task UpdateProfileAsync()
    {
        var profile = await _store.GetProfileAsync(_lifetime.Token); _localUser = profile.Id;
        _profile.Text = $"LanChat  /  {profile.DisplayName}";
        _avatar.Image?.Dispose(); _avatar.Image = null;
        if (profile.AvatarPath is not null && File.Exists(profile.AvatarPath))
        { using var image = Image.FromFile(profile.AvatarPath); _avatar.Image = new Bitmap(image); }
    }
    public async Task SelectConversationAsync(Guid id, Guid other)
    {
        if (_activeConversation != id) _messageLimit = 200;
        _activeConversation = id; _recipient = other; ShowPeople(false);
        await _store.SetConversationAsync(id, markRead: true, ct: _lifetime.Token); await RefreshAsync();
    }
    private async Task RunUiAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "LanChat", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    public async Task RefreshAsync()
    {
        if (_refreshing || IsDisposed || _lifetime.IsCancellationRequested) return;
        _refreshing = true;
        var selectedId = _activeConversation; var selectedRecipient = _recipient;
        try
        {
            var contacts = await _store.GetUsersAsync(_userSearch.Text.Trim(), _lifetime.Token);
            var conversations = await _store.GetConversationsAsync(_conversations.SearchText, _lifetime.Token);
            if (IsDisposed || _lifetime.IsCancellationRequested) return;
            _conversations.Bind(conversations, _activeConversation);
            _userRows.SuspendLayout();
            foreach (var id in _users.Keys.Except(contacts.Select(x => x.UserId)).ToArray()) { _users[id].Dispose(); _users.Remove(id); }
            foreach (var u in contacts)
            {
                if (!_users.TryGetValue(u.UserId, out var row))
                {
                    row = new UserItemControl(); row.Selected += async id => await RunUiAsync(async () => { var c = await _store.OpenDirectAsync(id, _lifetime.Token); await SelectConversationAsync(c, id); });
                    _users.Add(u.UserId, row); _userRows.Controls.Add(row);
                }
                row.Bind(u);
            }
            _userRows.ResumeLayout();
            var all = await _store.GetUsersAsync(ct: _lifetime.Token);
            if (selectedId.HasValue && selectedRecipient.HasValue && selectedId == _activeConversation)
            {
                var user = all.FirstOrDefault(x => x.UserId == selectedRecipient.Value);
                _chat.SelectConversation(user?.DisplayName ?? "Cuộc trò chuyện", user?.Presence == PresenceStatus.Online);
                var messages = await _store.GetMessagesAsync(selectedId.Value, ct: _lifetime.Token, limit: _messageLimit);
                if (IsDisposed) return;
                if (selectedId == _activeConversation)
                {
                    _chat.Bind(selectedId.Value, messages, _localUser);
                    _chat.SetHistoryAvailable(messages.Count >= _messageLimit);
                    if (ContainsFocus) await _store.SetConversationAsync(selectedId.Value, markRead: true, ct: _lifetime.Token);
                }
            }
            _status.Text = _health.Error ?? $"● Chat LAN trực tiếp    |    Peers: {all.Count(x => x.Presence == PresenceStatus.Online)}    |    Chờ gửi: {await _store.GetPendingCountAsync(_lifetime.Token)}    |    Lịch sử lưu trên máy này";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) _status.Text = "Không cập nhật được: " + ex.Message; }
        finally { _refreshing = false; }
    }
    private async Task ScanAsync(Button button)
    {
        if (_scanCancellation is not null) { _scanCancellation.Cancel(); return; }
        _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); button.Text = "Dừng quét";
        try { await _scanner.ScanAsync(_scanCancellation.Token); await RefreshAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "Không quét được mạng"); }
        finally { _scanCancellation.Dispose(); _scanCancellation = null; if (!button.IsDisposed) button.Text = "Quét lại mạng"; }
    }
}
