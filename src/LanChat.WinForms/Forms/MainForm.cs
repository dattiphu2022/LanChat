using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;
using Message = LanChat.Domain.Message;

namespace LanChat.WinForms.Forms;

public sealed partial class MainForm : DpiForm
{
    private readonly IChatStore _store;
    private readonly ILanScanner _scanner;
    private readonly IServiceProvider _services;
    private readonly NetworkHealth _health;
    private readonly ConversationListControl _conversations = new();
    private readonly ChatViewControl _chat = new();
    private readonly ConversationPanel _info = new();
    private readonly Panel _people = new() { Dock = DockStyle.Fill, Visible = false, BackColor = Color.White };
    private readonly PromptTextBox _userSearch = new() { Dock = DockStyle.Fill, PlaceholderText = "Tìm người dùng…" };
    private readonly Label _peopleEmpty = Theme.Label("Chưa tìm thấy người dùng trong mạng này.");
    private readonly Panel _userRows = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Dictionary<Guid, UserItemControl> _users = new();
    private readonly Dictionary<Guid, ComposerDraft> _drafts = new();
    private readonly PersonNameLabel _profile = Theme.NameLabel("LanChat", 10, true);
    private readonly Label _initial = Theme.Label("", 20, true);
    private readonly NavigationItemControl _networkNav = new() { Caption = "Mạng chat" };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _scanState = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private CancellationTokenSource? _scanCancellation;
    private Guid _localUser;
    private Guid? _activeConversation;
    private Guid? _searchAnchor;
    private bool _anchorScrolled;
    private bool _panelOpen;
    private bool _showPeople;
    private bool _layoutQueued;
    private bool _ticking;
    private Message[] _olderMessages = [];
    private Message[] _visibleMessages = [];
    private long _revision = -1;
    private long _presenceRevision = -1;
    private bool _hasOlder = true;
    private bool _loadingOlder;
    private Guid? _boundConversation;
    private Guid? _boundSearch;
    private Message[]? _boundOlder;
    private DateTime _lastPresence;
    private const int PageSize = 80;
    private IReadOnlyList<UserSummary> _contacts = [];
    private IReadOnlyList<ConversationSummary> _summaries = [];
    private readonly NotifyIcon _notification = new() { Icon = AppIcon.Value, Text = "LanChat", Visible = true };
    private Action<Guid, string, string>? _showNotification;
    private Func<bool>? _isSelectedNetwork;
    private Guid? _notificationConversation;
    private long? _incomingPosition;
    internal int UnreadCount { get; private set; }
    internal event Action? UnreadCountChanged;
    private readonly NavigationItemControl _messageNav = new() { Caption = "Tin nhắn", Active = true };
    private readonly NavigationItemControl _usersNav = new() { Caption = "Danh bạ" };
    private readonly TableLayoutPanel _workspace = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
    private readonly TableLayoutPanel _chatArea = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };

    public MainForm(IChatStore store, ILanScanner scanner, IServiceProvider services, NetworkHealth health, ChatImageService images)
    {
        _store = store; _scanner = scanner; _services = services; _health = health;
        _images = images; _chat.ConfigureImages(images);
        Icon = AppIcon.Value;
        Text = "LanChat — Chat nội bộ - phát triển bởi Trung tá Nguyễn Quốc Đạt";
        ClientSize = new Size(1240, 800); MinimumSize = new Size(660, 420);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Theme.Canvas;
        var root = Theme.Stack(2);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var workspace = _workspace;
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 292));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(workspace, 0, 0);

        var navigation = new BufferedPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Rail,
            Padding = new Padding(1, 12, 1, 0), Margin = Padding.Empty };
        var navLayout = Theme.Stack(8);
        navLayout.Dock = DockStyle.Top; navLayout.AutoSize = true; navLayout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        navLayout.BackColor = Theme.Rail;
        navLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        navLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        foreach (var label in new[] { _initial, _profile })
        {
            label.AutoSize = false; label.AutoEllipsis = true; label.UseMnemonic = false;
            label.TextAlign = ContentAlignment.MiddleCenter; label.ForeColor = Color.White;
            navLayout.Controls.Add(label);
        }
        var groups = new NavigationItemControl { Caption = "Nhóm" };
        var files = new NavigationItemControl { Caption = "Tệp" };
        var scan = new NavigationItemControl { Caption = "Quét lại" };
        foreach (var item in new[] { _messageNav, _usersNav, groups, files, scan, _networkNav })
        {
            item.Dock = DockStyle.Fill; item.MinimumSize = new Size(0, 48); item.Margin = Padding.Empty;
            navLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            navLayout.Controls.Add(item);
        }
        navigation.Controls.Add(navLayout);
        workspace.Controls.Add(navigation, 0, 0);
        var left = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 1, 0), BackColor = Theme.Border };
        left.Controls.Add(_conversations); left.Controls.Add(_people); workspace.Controls.Add(left, 1, 0);
        var peopleLayout = Theme.Stack(3); peopleLayout.Padding = new Padding(10);
        peopleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        peopleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        peopleLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        peopleLayout.Controls.Add(Theme.Label("Danh bạ LAN", 15, true), 0, 0);
        peopleLayout.Controls.Add(_userSearch, 0, 1); peopleLayout.Controls.Add(_userRows, 0, 2);
        _userRows.Controls.Add(_peopleEmpty);
        _people.Controls.Add(peopleLayout);
        _chatArea.Margin = Padding.Empty;
        _chatArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _chatArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _chatArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        _chatArea.Controls.Add(_chat, 0, 0); _chatArea.Controls.Add(_info, 1, 0);
        workspace.Controls.Add(_chatArea, 2, 0);
        var status = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = false };
        status.Items.AddRange([_status, _scanState]);
        root.Controls.Add(status, 0, 1); Controls.Add(root);

        _messageNav.Selected += (_, _) => ShowPeople(false);
        _usersNav.Selected += (_, _) => ShowPeople(true);
        groups.Selected += async (_, _) => await RunUiAsync(async () =>
        {
            using var form = ActivatorUtilities.CreateInstance<GroupsForm>(services);
            if (form.ShowDialog(this) == DialogResult.OK && form.SelectedGroup is Guid id)
            {
                await _store.ShowConversationAsync(id, _lifetime.Token);
                await SelectConversationAsync(id);
            }
        });
        files.Selected += async (_, _) => await RunUiAsync(OpenFilesAsync);
        scan.Selected += async (_, _) => await ScanAsync(scan);
        _networkNav.Selected += (_, _) => { using var form = ActivatorUtilities.CreateInstance<NetworkForm>(services); form.ShowDialog(this); };
        _userSearch.TextChanged += (_, _) => BindPeople();
        _conversations.SearchChanged += (_, _) => BindConversations();
        _conversations.Selected += async c => await RunUiAsync(async () =>
        {
            var id = c.Id != Guid.Empty ? c.Id : await _store.OpenDirectAsync(c.OtherUserId, _lifetime.Token);
            await SelectConversationAsync(id);
        });
        _conversations.ActionRequested += async (c, action) => await RunUiAsync(async () =>
        {
            if (c.Id == Guid.Empty) return;
            if (action == "delete")
            {
                if (MessageBox.Show(this, $"Xóa hội thoại “{new PersonName(c.DisplayName, c.Department)}” trên máy này?\n\nLịch sử trên máy khác vẫn được giữ. Hội thoại sẽ hiện lại khi có tin mới.",
                    "Xóa hội thoại phía tôi", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                await _store.DeleteConversationLocalAsync(c.Id, _lifetime.Token);
                _drafts.Remove(c.Id);
                if (_activeConversation == c.Id) ClearActiveConversation();
            }
            else
                await store.SetConversationAsync(c.Id, action == "pin" ? !c.IsPinned : (bool?)null,
                    action == "mute" ? !c.IsMuted : (bool?)null, action == "read", _lifetime.Token);
            await RefreshAsync();
        });
        _chat.SendRequested += SendSubmissionAsync;
        _chat.ActionRequested += async (m, action) => await RunUiAsync(() => MessageActionAsync(m, action));
        _chat.FileRequested += async file => await RunUiAsync(() => OpenAttachmentAsync(file));
        _chat.OlderRequested += async () => await RunUiAsync(LoadOlderAsync);
        _chat.ReadRequested += async () => await RunUiAsync(MarkActiveReadAsync);
        _chat.PanelRequested += async search => await RunUiAsync(() => OpenPanelAsync(search));
        _info.CloseRequested += () => SetPanel(false);
        _info.FileRequested += async file => await RunUiAsync(() => OpenAttachmentAsync(file));
        _info.SearchRequested += async query => await RunUiAsync(() => SearchInPanelAsync(query));
        _info.ResultSelected += async result => await RunUiAsync(() => JumpAsync(result));
        _chatArea.SizeChanged += (_, _) => QueueWorkspaceLayout();
        _workspace.SizeChanged += (_, _) => QueueWorkspaceLayout();
        Load += (_, _) => QueueWorkspaceLayout();
        DpiChanged += (_, _) => QueueWorkspaceLayout();
        DpiChangedAfterParent += (_, _) => QueueWorkspaceLayout();
        _timer.Tick += async (_, _) => await TickAsync();
        Shown += async (_, _) => { if (_showNotification is null) await StartViewAsync(); };
        Activated += async (_, _) => await RunUiAsync(MarkActiveReadAsync);
        _notification.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        _notification.BalloonTipClicked += async (_, _) =>
        {
            Show(); WindowState = FormWindowState.Normal; Activate();
            if (_notificationConversation is Guid id) await OpenNotificationAsync(id);
        };
        FormClosed += (_, _) =>
        {
            _timer.Stop(); _timer.Dispose(); _lifetime.Cancel(); _scanCancellation?.Cancel();
            _notification.Dispose();
        };
    }

    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.K)) { ShowPeople(false); _conversations.FocusSearch(); return true; }
        if (keyData == (Keys.Control | Keys.F))
        {
            _ = RunUiAsync(() => _activeConversation.HasValue ? OpenPanelAsync(true) : SearchMessagesAsync());
            return true;
        }
        if (keyData == Keys.Escape && _panelOpen) { SetPanel(false); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ShowPeople(bool value)
    {
        _showPeople = value;
        _people.Visible = value; _conversations.Visible = !value; _usersNav.Active = value; _messageNav.Active = !value;
        if (value) { _people.BringToFront(); BindPeople(); }
        else _conversations.BringToFront();
    }
    internal void EmbedInWorkspace(Action<Guid, string, string> notify, Func<bool> isSelectedNetwork)
    {
        TopLevel = false; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; MinimumSize = Size.Empty;
        _notification.Visible = false;
        _showNotification = notify;
        _isSelectedNetwork = isSelectedNetwork;
        _networkNav.Visible = false;
    }

    internal Task StartViewAsync() => RunUiAsync(async () =>
    {
        var ct = _lifetime.Token;
        _incomingPosition = (await Task.Run(() => _store.GetIncomingMessagesAsync(null, ct), ct)).Position;
        await UpdateProfileAsync();
        await RefreshAsync();
        if (!IsDisposed) _timer.Start();
    });

    internal Task OpenNotificationAsync(Guid id) => RunUiAsync(async () =>
    {
        await RefreshAsync();
        if (_summaries.Any(c => c.Id == id)) await SelectConversationAsync(id);
    });

    internal Task MarkReadOnActivationAsync() => RunUiAsync(MarkActiveReadAsync);

    internal async Task UpdateProfileAsync()
    {
        var profile = await Task.Run(() => _store.GetProfileAsync(_lifetime.Token), _lifetime.Token);
        if (IsDisposed) return;
        _localUser = profile.Id;
        _profile.SetName(new PersonName(profile.DisplayName, profile.Department));
        _initial.Text = string.Concat(profile.DisplayName.Trim().Take(1)).ToUpperInvariant();
    }

    public async Task SelectConversationAsync(Guid id)
    {
        if (_activeConversation != id)
        {
            if (_activeConversation is Guid old) _drafts[old] = _chat.Draft();
            _chat.RestoreDraft(_drafts.TryGetValue(id, out var draft) ? draft : null);
            _olderMessages = _visibleMessages = []; _searchAnchor = null; _hasOlder = true;
            _info.ResetSearch();
        }
        _activeConversation = id; ShowPeople(false);
        await RefreshAsync();
    }

    private void ClearActiveConversation()
    {
        _activeConversation = _boundConversation = _searchAnchor = null;
        _olderMessages = _visibleMessages = [];
        _hasOlder = true;
        _info.ResetSearch();
        _chat.ClearConversation();
        SetPanel(false);
    }

    private async Task RunUiAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, UiText.HideIpAddresses(ex.Message), "LanChat", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private async Task TickAsync()
    {
        if (_ticking || _refresh.CurrentCount == 0 || IsDisposed) return;
        _ticking = true;
        try
        {
            if (_revision != _store.Revision || _presenceRevision != _store.PresenceRevision || DateTime.UtcNow - _lastPresence > TimeSpan.FromSeconds(4))
                await RefreshAsync();
            else await UpdateStatusAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) _status.Text = UiText.HideIpAddresses(ex.Message); }
        finally { _ticking = false; }
    }

    public async Task RefreshAsync()
    {
        var ct = _lifetime.Token;
        await _refresh.WaitAsync(ct);
        try
        {
            var revision = _store.Revision;
            var presenceRevision = _store.PresenceRevision;
            var id = _activeConversation;
            var anchor = _searchAnchor;
            var previous = _olderMessages;
            var changed = _revision != revision;
            var presenceDue = DateTime.UtcNow - _lastPresence > TimeSpan.FromSeconds(4);
            IncomingMessageBatch? incoming = null;
            if (changed)
                incoming = await Task.Run(() => _store.GetIncomingMessagesAsync(_incomingPosition, ct), ct);
            if (changed || _presenceRevision != presenceRevision || presenceDue)
            {
                var contacts = await Task.Run(() => _store.GetUsersAsync(ct: ct), ct);
                var summaries = changed || _presenceRevision != presenceRevision
                    ? await Task.Run(() => _store.GetConversationsAsync(ct: ct), ct) : _summaries;
                if (IsDisposed) return;
                var presence = contacts.ToDictionary(u => u.UserId, u => u.Presence);
                _contacts = contacts;
                _summaries = summaries.Select(c => c.Kind == ConversationKind.Direct && presence.TryGetValue(c.OtherUserId, out var online)
                    ? c with { Presence = online } : c).ToArray();
                BindConversations(); if (_showPeople) BindPeople();
                UpdateUnreadCount();
                if (incoming is not null)
                {
                    _incomingPosition = incoming.Position;
                    NotifyIncoming(incoming.Messages);
                }
                _lastPresence = DateTime.UtcNow;
            }

            if (id.HasValue && id == _activeConversation && anchor == _searchAnchor)
            {
                var current = _summaries.FirstOrDefault(x => x.Id == id);
                if (current is null) ClearActiveConversation();
                else
                {
                    _chat.SelectConversation(new PersonName(current.DisplayName, current.Department), current.Presence == PresenceStatus.Online, current.Kind == ConversationKind.Group);
                    if (changed || _boundConversation != id || _boundSearch != anchor || _boundOlder != previous)
                    {
                        var data = await Task.Run(async () =>
                        {
                            var latest = anchor.HasValue ? await Distributed.GetMessageWindowAsync(id.Value, anchor.Value, ct) :
                                await _store.GetMessagesAsync(id.Value, ct: ct, limit: PageSize);
                            var cached = previous.Length == 0 || anchor.HasValue ? [] : await Distributed.GetMessageDetailsBatchAsync(previous.Select(m => m.Id).ToArray(), ct);
                            var messages = cached.Select(m => m.Message).Where(m => !m.IsDeletedLocal).Concat(latest)
                                .GroupBy(m => m.Id).Select(g => g.Last()).OrderBy(m => m.PhysicalTicks).ThenBy(m => m.LogicalCounter)
                                .ThenBy(m => m.OriginNodeId.ToString(), StringComparer.Ordinal).ThenBy(m => m.Id.ToString(), StringComparer.Ordinal).ToArray();
                            var details = await Distributed.GetMessageDetailsBatchAsync(latest.Select(m => m.Id).ToArray(), ct);
                            return (messages, details: cached.Concat(details).ToArray(), more: latest.Count >= PageSize);
                        }, ct);
                        if (IsDisposed) return;
                        if (id == _activeConversation && anchor == _searchAnchor)
                        {
                            _visibleMessages = data.messages;
                            _chat.Bind(id.Value, data.messages, _localUser, data.details, anchor.HasValue);
                            _boundConversation = id; _boundSearch = anchor; _boundOlder = previous;
                            _chat.SetHistoryAvailable(anchor.HasValue || (_hasOlder && (previous.Length > 0 || data.more)), anchor.HasValue);
                            if (anchor.HasValue && !_anchorScrolled) { _chat.FocusMessage(anchor.Value); _anchorScrolled = true; }
                            if (_panelOpen) await LoadPanelAsync(id.Value);
                            await MarkActiveReadAsync();
                        }
                    }
                }
            }
            _revision = revision; _presenceRevision = presenceRevision;
            await UpdateStatusAsync();
        }
        finally { _refresh.Release(); }
    }

    private void UpdateUnreadCount()
    {
        var total = _summaries.Sum(x => x.UnreadCount);
        if (UnreadCount == total) return;
        var hadUnread = UnreadCount > 0;
        UnreadCount = total;
        _messageNav.SetUnreadCount(total);
        if (_showNotification is null && hadUnread != (total > 0)) _notification.Icon = AppIcon.ForUnread(total > 0);
        UnreadCountChanged?.Invoke();
    }

    private void NotifyIncoming(IncomingMessageNotice[] messages)
    {
        if (messages.Length == 0) return;
        var reading = IsReadingActiveConversation;
        var available = _summaries.Where(c => !c.IsMuted).ToDictionary(c => c.Id);
        var pending = messages.Where(m => available.ContainsKey(m.ConversationId) && (m.ConversationId != _activeConversation || !reading)).ToArray();
        if (pending.Length == 0) return;
        var latest = pending[pending.Length - 1];
        var conversation = available[latest.ConversationId];
        var preview = latest.Preview.NormalizeLines(" ").Trim();
        if (preview.Length > 160) preview = preview.Substring(0, 160) + "…";
        var body = pending.Length > 1 ? $"{pending.Length} tin nhắn mới · {preview}" : preview;
        var title = new PersonName(conversation.DisplayName, conversation.Department).ToString();
        if (_showNotification is not null) _showNotification(conversation.Id, title, body);
        else
        {
            _notificationConversation = conversation.Id;
            _notification.ShowBalloonTip(3000, title, body, ToolTipIcon.Info);
        }
    }

    private bool IsReadingActiveConversation => Visible && (TopLevel ? Form.ActiveForm == this : Form.ActiveForm == TopLevelControl) &&
        WindowState != FormWindowState.Minimized &&
        (_isSelectedNetwork?.Invoke() ?? true) && !_showPeople && !_searchAnchor.HasValue &&
        _boundConversation == _activeConversation && _chat.Visible && _chat.AtBottom;

    private Task MarkActiveReadAsync()
    {
        var active = _summaries.FirstOrDefault(c => c.Id == _activeConversation);
        return IsReadingActiveConversation &&
            active is { UnreadCount: > 0 } ? _store.SetConversationAsync(active.Id, markRead: true, ct: _lifetime.Token) : Task.CompletedTask;
    }

    private async Task UpdateStatusAsync()
    {
        var ct = _lifetime.Token;
        var state = await Task.Run(async () => (await Consensus.GetStatusAsync(ct), await _store.GetPendingCountAsync(ct)), ct);
        if (!IsDisposed)
        {
            var online = _contacts.Count(x => x.Presence == PresenceStatus.Online);
            _status.Text = UiText.HideIpAddresses(_health.Error ?? (online == 0 ? _health.DiscoveryIssue : null)
                ?? $"{state.Item1.StatusText}   ·   {online} máy trực tuyến   ·   {state.Item2} tin chờ");
        }
    }

    private void BindConversations()
    {
        var text = _conversations.SearchText;
        IEnumerable<ConversationSummary> matches = _summaries;
        if (text.Length > 0)
        {
            matches = matches.Where(c => ChatStore.NameMatches(new PersonName(c.DisplayName, c.Department).ToString(), text));
            matches = matches.Concat(_contacts.Where(u => ChatStore.NameMatches(new PersonName(u.DisplayName, u.Department).ToString(), text) &&
                !_summaries.Any(c => c.OtherUserId == u.UserId))
                .Select(u => new ConversationSummary(Guid.Empty, u.UserId, u.DisplayName, "Bắt đầu trò chuyện", 0, 0, false, false, u.Presence)
                    { Department = u.Department }));
        }
        _conversations.Bind(matches.ToArray(), _activeConversation);
    }

    private void BindPeople()
    {
        var query = _userSearch.Text.Trim();
        var contacts = _contacts.Where(u => query.Length == 0 || ChatStore.NameMatches(new PersonName(u.DisplayName, u.Department).ToString(), query)).ToArray();
        _userRows.SuspendLayout();
        foreach (var id in _users.Keys.Except(contacts.Select(x => x.UserId)).ToArray()) { _users[id].Dispose(); _users.Remove(id); }
        for (var index = 0; index < contacts.Length; index++)
        {
            var u = contacts[index];
            if (!_users.TryGetValue(u.UserId, out var row))
            {
                row = new UserItemControl();
                row.Selected += async user => await RunUiAsync(async () =>
                {
                    var c = await _store.OpenDirectAsync(user, _lifetime.Token);
                    await SelectConversationAsync(c);
                });
                _users.Add(u.UserId, row);
                Theme.AddAtCurrentDpi(_userRows, row);
            }
            row.Bind(u);
            var targetIndex = contacts.Length - index - 1;
            if (_userRows.Controls.GetChildIndex(row) != targetIndex) _userRows.Controls.SetChildIndex(row, targetIndex);
        }
        _peopleEmpty.Text = _contacts.Count == 0 ? "Chưa tìm thấy người dùng trong mạng này." : "Không có người dùng khớp từ khóa.";
        _peopleEmpty.Visible = contacts.Length == 0;
        _userRows.ResumeLayout();
    }

    private async Task LoadOlderAsync()
    {
        if (_loadingOlder || _activeConversation is not Guid id) return;
        _loadingOlder = true;
        try
        {
            if (_searchAnchor.HasValue) { _searchAnchor = null; _olderMessages = []; _hasOlder = true; await RefreshAsync(); _chat.ScrollToEnd(); return; }
            var first = _visibleMessages.FirstOrDefault();
            if (first is null || !_hasOlder) return;
            var ct = _lifetime.Token;
            var page = await Task.Run(() => _store.GetOlderMessagesAsync(id, first, PageSize, ct), ct);
            if (id != _activeConversation || IsDisposed) return;
            _hasOlder = page.Length == PageSize;
            _olderMessages = page.Concat(_visibleMessages).ToArray();
            await RefreshAsync();
        }
        finally { _loadingOlder = false; }
    }

    private void SetPanel(bool visible)
    {
        _panelOpen = visible;
        _info.Visible = visible;
        LayoutPanel();
    }
    private bool IsCompactChat => _chatArea.ClientSize.Width < this.ScalePixels(660);

    private void QueueWorkspaceLayout()
    {
        if (_layoutQueued || !IsHandleCreated || Disposing) return;
        _layoutQueued = true;
        BeginInvoke((Action)(() =>
        {
            try
            {
                if (IsDisposed) return;
                LayoutWorkspace();
                LayoutPanel();
            }
            finally { _layoutQueued = false; }
        }));
    }

    private void LayoutWorkspace()
    {
        var width = _workspace.ClientSize.Width;
        var preferredListWidth = this.ScalePixels(width < this.ScalePixels(1000) ? 248 : 292);
        var listWidth = Math.Min(preferredListWidth, width * 3 / 10);
        if (_workspace.ColumnStyles[1].Width != listWidth) _workspace.ColumnStyles[1].Width = listWidth;
    }
    private void LayoutPanel()
    {
        var width = !_panelOpen ? 0 : IsCompactChat ? _chatArea.ClientSize.Width : this.ScalePixels(288);
        if (_chatArea.ColumnStyles[1].Width != width) _chatArea.ColumnStyles[1].Width = width;
        _chat.Visible = !_panelOpen || !IsCompactChat;
    }
    private async Task OpenPanelAsync(bool search)
    {
        if (_activeConversation is not Guid id) return;
        if (!search && _panelOpen) { SetPanel(false); return; }
        SetPanel(true);
        if (search) _info.FocusSearch();
        await LoadPanelAsync(id);
    }
    private async Task LoadPanelAsync(Guid id)
    {
        var ct = _lifetime.Token;
        var data = await Task.Run(async () => (await Distributed.GetAttachmentsAsync(id, ct), (await Distributed.GetGroupsAsync(ct)).FirstOrDefault(g => g.Id == id)), ct);
        if (id == _activeConversation && !IsDisposed) _info.Bind(data.Item1, data.Item2, _contacts, _localUser, _profile.Person);
    }
    private async Task SearchInPanelAsync(string query)
    {
        if (_activeConversation is not Guid id) return;
        var ct = _lifetime.Token;
        var results = await Task.Run(() => Distributed.SearchMessagesAsync(query, id, ct), ct);
        if (!IsDisposed && id == _activeConversation) _info.SetResults(query, results);
    }
    private async Task JumpAsync(SearchResult result)
    {
        if (_activeConversation != result.ConversationId) return;
        _searchAnchor = result.MessageId; _anchorScrolled = false;
        if (IsCompactChat) SetPanel(false);
        await RefreshAsync();
    }

    private async Task ScanAsync(NavigationItemControl button)
    {
        if (_scanCancellation is not null) { _scanCancellation.Cancel(); return; }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _scanCancellation = cancellation; button.Caption = "Dừng quét";
        try
        {
            var progress = new Progress<LanScanProgress>(p =>
            {
                if (!IsDisposed && _scanCancellation == cancellation)
                    _scanState.Text = p.CheckingUdp ? "Đang tìm máy qua UDP…" : $"Đã kiểm tra {p.Checked:N0} địa chỉ · tìm thấy {p.Discovered} máy";
            });
            var peers = await _scanner.ScanAsync(cancellation.Token, progress);
            if (!IsDisposed) { _scanState.Text = peers.Count == 0 ? "Chưa tìm thấy máy" : $"Tìm thấy {peers.Count} máy"; await RefreshAsync(); }
        }
        catch (OperationCanceledException) { if (!IsDisposed) _scanState.Text = "Đã dừng quét"; }
        catch (Exception ex) { if (!IsDisposed) _scanState.Text = "Quét mạng: " + UiText.HideIpAddresses(ex.Message); }
        finally { _scanCancellation = null; cancellation.Dispose(); if (!IsDisposed) button.Caption = "Quét lại"; }
    }
}
