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
    private readonly DraftImageStore _draftImages;
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
    private bool _jumpedInLatest;
    private long _historyRequest;
    private bool _panelOpen;
    private long _panelRequest;
    private long _panelCommitIndex;
    private long _memberSaveIndex;
    private bool _savingMembers;
    private bool _groupLeaderReady;
    private string? _memberSaveError;
    private bool _showPeople;
    private bool _layoutQueued;
    private bool _ticking;
    private int _userOperations;
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
    internal bool HasUnsentDrafts => _chat.Draft().HasContent || _drafts.Values.Any(draft => draft.HasContent);
    internal bool HasActiveWork => _chat.ComposerWorking || _scanCancellation is not null || _userOperations > 0;
    internal event Action? UnreadCountChanged;
    private readonly NavigationItemControl _messageNav = new() { Caption = "Tin nhắn", Active = true };
    private readonly NavigationItemControl _usersNav = new() { Caption = "Danh bạ" };
    private readonly TableLayoutPanel _workspace = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
    private readonly TableLayoutPanel _chatArea = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };

    public MainForm(IChatStore store, ILanScanner scanner, IServiceProvider services, NetworkHealth health, ChatImageService images)
    {
        _store = store; _scanner = scanner; _services = services; _health = health;
        _images = images; _chat.ConfigureImages(images);
        _draftImages = new DraftImageStore(services.GetRequiredService<NodeOptions>().DataDirectory);
        _chat.ConfigureDraftImages(_draftImages, _lifetime.Token);
        _chat.DraftFilesChanged += CleanupDraftImages;
        Icon = AppIcon.Value;
        Text = $"LanChat — Chat nội bộ - phát triển bởi {AboutForm.AuthorName}";
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
                else CleanupDraftImages();
            }
            else
                await store.SetConversationAsync(c.Id, action == "pin" ? !c.IsPinned : (bool?)null,
                    action == "mute" ? !c.IsMuted : (bool?)null, action == "read", _lifetime.Token);
            await RefreshAsync();
        });
        _chat.SendRequested += SendSubmissionAsync;
        _chat.ActionRequested += async (m, action) => await RunUiAsync(() => MessageActionAsync(m, action), userOperation: true);
        _chat.QuoteRequested += async (conversation, message) => await RunUiAsync(() => JumpToMessageAsync(conversation, message, useLoadedHistory: true));
        _chat.FileRequested += async file => await RunUiAsync(() => OpenAttachmentAsync(file), userOperation: true);
        _chat.OlderRequested += async () => await RunUiAsync(LoadOlderAsync);
        _chat.ReadRequested += async () => await RunUiAsync(MarkActiveReadAsync);
        _chat.PanelRequested += async search => await RunUiAsync(() => OpenPanelAsync(search));
        _info.CloseRequested += () => SetPanel(false);
        _info.FileRequested += async file => await RunUiAsync(() => OpenAttachmentAsync(file), userOperation: true);
        _info.SearchRequested += async query => await RunUiAsync(() => SearchInPanelAsync(query));
        _info.ResultSelected += async result => await RunUiAsync(() => JumpAsync(result));
        _info.MemberSaveRequested += async changes => await RunUiAsync(() => SavePanelMembersAsync(changes), userOperation: true);
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
        Disposed += (_, _) =>
        {
            _timer.Stop(); _timer.Dispose(); _lifetime.Cancel(); _scanCancellation?.Cancel();
            _draftImages.Dispose();
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
            if (_activeConversation is Guid old && _chat.Draft() is { HasContent: true } previousDraft) _drafts[old] = previousDraft;
            _drafts.TryGetValue(id, out var draft);
            _drafts.Remove(id);
            _chat.RestoreDraft(draft);
            _boundConversation = null;
            _olderMessages = _visibleMessages = []; _searchAnchor = null; _jumpedInLatest = false; _hasOlder = true;
            _historyRequest++;
            ResetConversationPanel(_summaries.Any(conversation => conversation.Id == id && conversation.Kind == ConversationKind.Group));
        }
        _activeConversation = id; ShowPeople(false);
        await RefreshAsync();
    }

    private void ClearActiveConversation()
    {
        _activeConversation = _boundConversation = _searchAnchor = null;
        _historyRequest++;
        _jumpedInLatest = false;
        _olderMessages = _visibleMessages = [];
        _hasOlder = true;
        ResetConversationPanel(isGroup: false);
        _chat.ClearConversation();
        SetPanel(false);
    }

    private void CleanupDraftImages()
    {
        if (!_chat.ComposerWorking && !IsDisposed)
            _draftImages.Prune(_drafts.Values.SelectMany(draft => draft.Files).Concat(_chat.Draft().Files));
    }

    private async Task RunUiAsync(Func<Task> action, bool userOperation = false)
    {
        if (userOperation) _userOperations++;
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, UiText.HideIpAddresses(ex.Message), "LanChat", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { if (userOperation) _userOperations--; }
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
            var refreshContacts = changed || _presenceRevision != presenceRevision || presenceDue;
            IncomingMessageBatch? incoming = null;
            if (changed)
                incoming = await Task.Run(() => _store.GetIncomingMessagesAsync(_incomingPosition, ct), ct);
            if (refreshContacts)
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
                    var refreshHistory = changed || _boundConversation != id || _boundSearch != anchor || _boundOlder != previous;
                    if (refreshHistory)
                    {
                        var data = await ReadHistoryAsync(id.Value, anchor, previous, ct);
                        if (IsDisposed) return;
                        if (id == _activeConversation && anchor == _searchAnchor && previous == _olderMessages)
                        {
                            BindHistory(id.Value, anchor, previous, data);
                            await MarkActiveReadAsync();
                        }
                    }
                    if (_panelOpen && id == _activeConversation &&
                        (refreshHistory || (refreshContacts && current.Kind == ConversationKind.Group)))
                        await LoadPanelAsync(id.Value);
                }
            }
            _revision = revision; _presenceRevision = presenceRevision;
            await UpdateStatusAsync();
        }
        finally { _refresh.Release(); }
    }

    private Task<(Message[] Messages, MessageDetails[] Details, bool More)> ReadHistoryAsync(
        Guid conversation, Guid? anchor, Message[] previous, CancellationToken ct) => Task.Run(async () =>
    {
        var latest = anchor is Guid id ? await Distributed.GetMessageWindowAsync(conversation, id, ct) :
            await _store.GetMessagesAsync(conversation, ct: ct, limit: PageSize);
        var cached = anchor.HasValue ? Array.Empty<Message>() : previous;
        var ids = cached.Select(m => m.Id).Concat(latest.Select(m => m.Id)).Distinct().ToArray();
        var details = await Distributed.GetMessageDetailsBatchAsync(ids, ct);
        var messages = details.Select(m => m.Message).Where(m => !m.IsDeletedLocal)
            .OrderBy(m => m.PhysicalTicks).ThenBy(m => m.LogicalCounter)
            .ThenBy(m => m.OriginNodeId.ToString(), StringComparer.Ordinal).ThenBy(m => m.Id.ToString(), StringComparer.Ordinal).ToArray();
        return (messages, details, latest.Count >= PageSize);
    }, ct);

    private void BindHistory(Guid conversation, Guid? anchor, Message[] previous, (Message[] Messages, MessageDetails[] Details, bool More) data)
    {
        _visibleMessages = data.Messages;
        _chat.Bind(conversation, data.Messages, _localUser, data.Details, anchor.HasValue);
        _boundConversation = conversation;
        _boundSearch = anchor;
        _boundOlder = previous;
        _chat.SetHistoryAvailable(_hasOlder && (previous.Length > 0 || data.More), anchor.HasValue || _jumpedInLatest);
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
        (_isSelectedNetwork?.Invoke() ?? true) && !_showPeople && !_searchAnchor.HasValue && !_jumpedInLatest &&
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
            if (_groupLeaderReady != state.Item1.LeaderReady)
            {
                _groupLeaderReady = state.Item1.LeaderReady;
                if (_panelOpen) UpdatePanelMemberActions();
            }
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
            var request = ++_historyRequest;
            if (_searchAnchor.HasValue || _jumpedInLatest)
            {
                _searchAnchor = null;
                _jumpedInLatest = false;
                _olderMessages = [];
                _boundOlder = null;
                _hasOlder = true;
                _chat.ClearHighlight();
                _chat.SetHistoryAvailable(false);
                await RefreshAsync();
                if (IsCurrentHistory(id, request))
                {
                    _chat.ScrollToEnd();
                    await MarkActiveReadAsync();
                }
                return;
            }
            var first = _visibleMessages.FirstOrDefault();
            if (first is null || !_hasOlder) return;
            var ct = _lifetime.Token;
            var page = await Task.Run(() => _store.GetOlderMessagesAsync(id, first, PageSize, ct), ct);
            if (!IsCurrentHistory(id, request)) return;
            _hasOlder = page.Length == PageSize;
            _olderMessages = page.Concat(_visibleMessages).ToArray();
            await RefreshAsync();
        }
        finally { _loadingOlder = false; }
    }

    private void SetPanel(bool visible)
    {
        if (!visible) _panelRequest++;
        _panelOpen = visible;
        _info.Visible = visible;
        LayoutPanel();
    }

    private void ResetConversationPanel(bool isGroup)
    {
        _panelRequest++;
        _memberSaveError = null;
        _info.ResetConversation(isGroup);
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
        var request = ++_panelRequest;
        var isGroup = _summaries.Any(conversation => conversation.Id == id && conversation.Kind == ConversationKind.Group);
        var data = await Task.Run(async () =>
        {
            var attachments = await Distributed.GetAttachmentsAsync(id, ct);
            if (!isGroup) return (attachments, status: (ConsensusStatus?)null, groups: (GroupSummary[]?)null);
            // Read the local commit index before the membership it describes.
            var status = await Consensus.GetStatusAsync(ct);
            var groups = await Distributed.GetGroupsAsync(ct);
            return (attachments, status, groups);
        }, ct);
        if (IsDisposed || !_panelOpen || id != _activeConversation || request != _panelRequest) return;
        if (data.status is { } state)
        {
            _panelCommitIndex = state.CommitIndex;
            _groupLeaderReady = state.LeaderReady;
        }
        _info.Bind(data.attachments, data.groups, id, _contacts, _localUser, _profile.Person);
        UpdatePanelMemberActions();
    }

    private bool WaitingForMemberCommit => _memberSaveIndex > _panelCommitIndex;

    private void UpdatePanelMemberActions()
    {
        var allowChecks = !_savingMembers && !WaitingForMemberCommit;
        var status = _savingMembers ? "Đang lưu thành viên…" :
            WaitingForMemberCommit ? "Đang đồng bộ thay đổi nhóm…" : _memberSaveError;
        _info.SetMemberActions(allowChecks, allowChecks && _groupLeaderReady, status);
    }

    private async Task SavePanelMembersAsync(GroupMemberChanges changes)
    {
        if (!_panelOpen || changes.GroupId != _activeConversation || changes.ActorUserId != _localUser ||
            _savingMembers || WaitingForMemberCommit || !_groupLeaderReady) return;
        var ct = _lifetime.Token;
        _savingMembers = true;
        _memberSaveError = null;
        UpdatePanelMemberActions();
        try
        {
            await changes.ApplyAsync(Distributed, async command =>
            {
                var result = await Consensus.ProposeAsync(command, ct);
                if (!result.Success)
                    throw new InvalidOperationException(result.Error ?? "Chưa xác nhận được thay đổi nhóm.");
                _memberSaveIndex = Math.Max(_memberSaveIndex, result.Index);
            }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (changes.GroupId == _activeConversation) _memberSaveError = UiText.HideIpAddresses(ex.Message);
        }
        finally
        {
            try
            {
                if (!ct.IsCancellationRequested && !IsDisposed && _panelOpen && _activeConversation is Guid current)
                    await LoadPanelAsync(current);
            }
            finally
            {
                _savingMembers = false;
                if (!IsDisposed) UpdatePanelMemberActions();
            }
        }
    }

    private async Task SearchInPanelAsync(string query)
    {
        if (_activeConversation is not Guid id) return;
        var ct = _lifetime.Token;
        var results = await Task.Run(() => Distributed.SearchMessagesAsync(query, id, ct), ct);
        if (!IsDisposed && id == _activeConversation) _info.SetResults(query, results);
    }
    private Task JumpAsync(SearchResult result) => JumpToMessageAsync(result.ConversationId, result.MessageId);

    private bool IsCurrentHistory(Guid conversation, long request) =>
        !IsDisposed && conversation == _activeConversation && request == _historyRequest && (_isSelectedNetwork?.Invoke() ?? true);

    private async Task JumpToMessageAsync(Guid conversation, Guid message, bool useLoadedHistory = false)
    {
        if (_activeConversation != conversation) return;
        var request = ++_historyRequest;
        var ct = _lifetime.Token;
        await _refresh.WaitAsync(ct);
        try
        {
            if (!IsCurrentHistory(conversation, request)) return;
            if (useLoadedHistory && _boundConversation == conversation && _visibleMessages.Any(m => m.Id == message))
            {
                var latest = await Task.Run(() => Distributed.GetMessageDetailsBatchAsync([message], ct), ct);
                if (!IsCurrentHistory(conversation, request) || !latest.Any(d => CanJump(d.Message))) return;
                if (IsCompactChat) SetPanel(false);
                if (_chat.FocusMessage(message))
                {
                    _jumpedInLatest = !_searchAnchor.HasValue;
                    _chat.SetHistoryAvailable(false, returnToLatest: true);
                    return;
                }
            }

            var data = await ReadHistoryAsync(conversation, message, [], ct);
            if (!IsCurrentHistory(conversation, request)) return;
            if (!data.Messages.Any(CanJump)) return;

            if (IsCompactChat) SetPanel(false);
            _searchAnchor = message;
            _jumpedInLatest = false;
            BindHistory(conversation, message, _olderMessages, data);
            _chat.FocusMessage(message);
        }
        finally { _refresh.Release(); }

        bool CanJump(Message target) => target.Id == message && target.ConversationId == conversation && !target.IsRecalled && !target.IsDeletedLocal;
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
