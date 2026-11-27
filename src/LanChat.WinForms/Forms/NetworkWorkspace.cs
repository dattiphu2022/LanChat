using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace LanChat.WinForms.Forms;

internal sealed partial class NetworkWorkspace : DpiForm
{
    private readonly NetworkCatalog _catalog;
    private readonly CancellationTokenSource _appearanceLifetime = new();
    private int _appearanceRequest;
    private readonly WindowsStartup _startup;
    private readonly IconTabControl _tabs = new(UiIcon.Network, UiIcon.NetworkUnread)
        { Dock = DockStyle.Fill, ShowToolTips = true };
    private readonly Dictionary<Guid, (RunningNetwork Network, MainForm View, TabPage Tab)> _running = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, IServiceProvider> _updateNetworks = new();
    private readonly IconButton _scanButton = Theme.Secondary("Quét tất cả", UiIcon.Scan);
    private IconButton? _moreButton;
    private readonly System.Windows.Forms.Timer _unreadReminder = new();
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 60 * 1000 };
    private readonly Button _restoreDefaultButton;
    private readonly Button _manageNetworksButton;
    private readonly Label _feedback = Theme.Label("");
    private readonly NotifyIcon _tray = new() { Icon = AppIcon.Value, Text = "LanChat", Visible = true };
    private readonly List<Button> _actions = [];
    private readonly List<Button> _networkActions = [];
    private bool _busy, _exiting, _closed, _exitRequested;
    private bool _restartAsAdministrator;
    internal bool RestartAsAdministrator { get; private set; }

    private FormWindowState _restoreState;
    private (Guid NetworkId, Guid ConversationId, Guid? MessageId)? _notificationTarget;
    private (Guid NetworkId, Guid ConversationId, Guid? MessageId)? _pendingNotification;

    public NetworkWorkspace(NetworkCatalog catalog, WindowsStartup startup, UpdateService updates)
    {
        _catalog = catalog; _startup = startup;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += WindowsAppearanceChanged;
        Disposed += ReleaseAppearance;
        _updates = new UpdateCoordinator(updates);
        updates.ResolveNetwork = id => _updateNetworks.TryGetValue(id, out var services) ? services : null;
        Icon = AppIcon.Value;
        _updates.Changed += RefreshWindowTitle;
        ClientSize = new Size(1260, 840); MinimumSize = new Size(660, 450);
        StartPosition = FormStartPosition.CenterScreen;
        var root = Theme.Stack(3);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = Theme.Toolbar();
        actions.Padding = new Padding(6, 2, 6, 2); actions.Margin = Padding.Empty;
        void Compact(Button button)
        { button.Font = Theme.GetFont(9, false); button.MinimumSize = new Size(0, 28); button.Padding = new Padding(6, 1, 6, 1); button.Margin = new Padding(2, 1, 2, 1); }
        Compact(_scanButton);
        Compact(_updateButton);
        Compact(_cancelUpdateButton);
        var actionTips = new ToolTip();
        actionTips.SetToolTip(_scanButton, "Tìm lại máy trên tất cả mạng chat đang mở; bấm lần nữa để dừng quét.");
        Disposed += (_, _) => actionTips.Dispose();
        _feedback.Visible = false; _feedback.Margin = new Padding(8, 0, 8, 2);
        _feedback.TextChanged += (_, _) => _feedback.Visible = _feedback.Text.Length > 0;
        _feedback.UseMnemonic = false;
        actions.SizeChanged += (_, _) => _feedback.MaximumSize = new Size(Math.Max(1, actions.ClientSize.Width - 16), 0);
        Button AddAction(string label, UiIcon icon, Func<Task> action, bool changesNetworks = false)
        {
            var button = Theme.Secondary(label, icon);
            Compact(button);
            button.Click += async (_, _) => await RunAsync(action);
            actions.Controls.Add(button);
            _actions.Add(button);
            if (changesNetworks) _networkActions.Add(button);
            return button;
        }
        _scanButton.Click += async (_, _) => { if (_scan is not null) _scan.Cancel(); else if (!_busy && !_installing) await ScanAsync(); };
        actions.Controls.Add(_scanButton);
        _manageNetworksButton = AddAction("Mạng chat", UiIcon.Network, ManageAsync, changesNetworks: true);
        _restoreDefaultButton = AddAction("Khôi phục mạng mặc định", UiIcon.Restore, RestoreDefaultNetworkAsync, changesNetworks: true);
        _restoreDefaultButton.Visible = _catalog.CanRestoreDefaultNetwork && !_catalog.Entries.Any(_catalog.IsDefault);
        AddAction("Cài đặt", UiIcon.Settings, SettingsAsync);
        var moreMenu = new ThemeMenuStrip();
        moreMenu.Items.Add(_aboutMenuItem);
        _aboutMenuItem.Click += async (_, _) => await RunAsync(ShowAboutAsync);
        moreMenu.Items.Add("Cài đặt cập nhật…", UiIcons.ForMenu(UiIcon.Settings), async (_, _) => await RunAsync(() => { ShowUpdateSettings(); return Task.CompletedTask; }));
        moreMenu.Items.Add("Hướng dẫn bắt đầu…", UiIcons.ForMenu(UiIcon.Book), async (_, _) => await RunAsync(ShowGettingStartedAsync));
        _moreButton = (IconButton)AddAction("Thêm", UiIcon.More, () =>
        { moreMenu.Show(_moreButton!, new Point(0, _moreButton!.Height)); return Task.CompletedTask; });
        _moreButton.ContextMenuStrip = moreMenu;
        _moreButton.AccessibleName = "Thêm: giới thiệu, cập nhật và hướng dẫn";
        RefreshWindowTitle();
        actionTips.SetToolTip(_moreButton, _moreButton.AccessibleName);
        actions.Controls.Add(_cancelUpdateButton);
        actions.Controls.Add(_updateButton);
        _updateButton.Click += async (_, _) => await ApplyToolbarUpdateAsync();
        _cancelUpdateButton.Click += (_, _) => _updates.CancelDownload();
        Disposed += (_, _) => moreMenu.Dispose();
        root.Controls.Add(actions, 0, 0); root.Controls.Add(_feedback, 0, 1);
        root.Controls.Add(_tabs, 0, 2);
        Controls.Add(root);
        var trayMenu = new ThemeMenuStrip();
        trayMenu.Items.Add("Mở LanChat", UiIcons.ForMenu(UiIcon.Open), (_, _) => RestoreWindow());
        trayMenu.Items.Add("Thoát LanChat", UiIcons.ForMenu(UiIcon.Exit), (_, _) => { _exitRequested = true; Close(); });
        trayMenu.Items.Insert(1, new ToolStripMenuItem("Hướng dẫn bắt đầu…", UiIcons.ForMenu(UiIcon.Book), async (_, _) => await RunAsync(ShowGettingStartedAsync)));
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => RestoreWindow();
        _tray.BalloonTipClicked += async (_, _) =>
        {
            _pendingNotification = _notificationTarget;
            RestoreWindow();
            await OpenPendingNotificationAsync();
        };
        Activated += async (_, _) =>
        {
            await MarkSelectedReadAsync();
            if (!IsDisposed) BeginInvoke((Action)(async () => await OpenPendingNotificationAsync()));
        };
        _tabs.SelectedIndexChanged += async (_, _) => await MarkSelectedReadAsync();
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) Hide();
            else _restoreState = WindowState;
        };
        _unreadReminder.Tick += (_, _) => ShowUnreadReminder();
        _updateTimer.Tick += async (_, _) => { if (!_exiting && !_installing) await _updates.CheckAsync(manual: false); };
        FormClosed += (_, _) => { _updateTimer.Stop(); _updateTimer.Dispose(); trayMenu.Dispose(); _updates.Changed -= RefreshWindowTitle; _updates.Dispose(); };
        FormClosing += ClosingAsync;
    }

    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
    {
        if ((keyData == (Keys.Control | Keys.F) || keyData == (Keys.Control | Keys.K)) &&
            _tabs.Enabled && _tabs.SelectedTab?.Tag is Guid id && _running.TryGetValue(id, out var item))
        {
            item.View.FocusConversationSearch();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void RestoreWindow()
    {
        if (_exiting) return;
        Show(); WindowState = _restoreState;
        (UiInteraction.ForegroundDialog(this) ?? this).Activate();
    }

    private async Task OpenPendingNotificationAsync()
    {
        if (_exiting || _busy || UiInteraction.ForegroundDialog(this) is not null ||
            _running.Values.Any(item => item.View.HasDialogOperation) || _pendingNotification is not { } target) return;
        _pendingNotification = null;
        if (!_running.TryGetValue(target.NetworkId, out var item)) return;
        _tabs.SelectedTab = item.Tab;
        await item.View.OpenNotificationAsync(target.ConversationId, target.MessageId);
    }

    private void UpdateActionState()
    {
        foreach (var button in _actions)
            button.Enabled = !_busy && !_installing && !_exiting && (_scan is null || !_networkActions.Contains(button));
        _scanButton.Enabled = !_exiting && !_installing && (_scan is not null || (!_busy && _running.Count > 0));
        _tabs.Enabled = !_installing && !_exiting;
        RefreshUpdateButton();
    }

    internal void ExitApplication() { _exitRequested = true; Close(); }

    public async Task StartAsync()
    {
        _busy = true;
        UpdateActionState();
        try { await LoadNetworksAsync(); }
        finally { _busy = false; UpdateActionState(); }
        _ = _updates.CheckAsync(manual: false);
        _updateTimer.Start();
        if (!_catalog.UiPreferences.OnboardingCompleted && (!_catalog.HasSavedProfile || _running.Count == 0))
            await ShowGettingStartedAsync();
    }

    private async Task LoadNetworksAsync()
    {
        await _catalog.LoadAsync();
        await ApplyConfiguredThemeAsync();
        _restoreDefaultButton.Visible = _catalog.CanRestoreDefaultNetwork;
        var failures = new List<string>();
        if (_catalog.DefaultNetworkError is string defaultError) failures.Add(defaultError);
        foreach (var entry in _catalog.Entries.OrderByDescending(e => e.Primary))
        {
            try { await OpenNetworkAsync(entry, allowUpgrade: true); }
            catch (Exception ex) { failures.Add(entry.Name + " · " + UiText.HideIpAddresses(ex.Message)); }
        }
        if (_catalog.DefaultNetwork is { } selectedDefault && _running.TryGetValue(selectedDefault.Id, out var defaultNetwork))
            _tabs.SelectedTab = defaultNetwork.Tab;
        _feedback.Text = failures.Count > 0 ? string.Join("  |  ", failures) :
            _running.Count == 0 ? NoNetworkMessage : "";
    }

    private string NoNetworkMessage => _catalog.CanRestoreDefaultNetwork
        ? "Chọn Khôi phục mạng mặc định để tiếp tục chat."
        : "Mở Mạng chat rồi chọn Thêm mã hoặc Tạo mạng để bắt đầu.";

    private async Task OpenNetworkAsync(NetworkEntry entry, bool allowUpgrade = false)
    {
        if (_running.ContainsKey(entry.Id)) return;
        var network = await _catalog.StartAsync(entry, allowUpgrade, this);
        TabPage? tab = null;
        MainForm? view = null;
        try
        {
            view = ActivatorUtilities.CreateInstance<MainForm>(network.Services);
            tab = _tabs.CreatePage(entry.Name, UiIcon.Network);
            tab.Tag = entry.Id;
            view.EmbedInWorkspace((conversationId, messageId, title, body) => ShowNotification(entry.Id, conversationId, messageId, title, body),
                () => Visible && WindowState != FormWindowState.Minimized && _tabs.SelectedTab == tab);
            view.ApplyPreferences(_catalog.UiPreferences);
            view.ManageNetworksRequested += async () => await RunAsync(ManageAsync);
            view.Dock = DockStyle.Fill;
            tab.Controls.Add(view); _tabs.TabPages.Add(tab);
            _running.Add(entry.Id, (network, view, tab));
            _manageNetworksButton.Visible = false;
            view.UnreadCountChanged += UpdateUnreadBadges;
            view.DialogOperationCompleted += async () => await OpenPendingNotificationAsync();
            view.Show();
            await view.StartViewAsync();
            _updateNetworks[entry.Id] = network.Services;
            _updates.Service.Packages.ResumeNetwork(entry.Id);
        }
        catch
        {
            _running.Remove(entry.Id);
            _manageNetworksButton.Visible = _running.Count == 0;
            _updateNetworks.TryRemove(entry.Id, out _);
            if (tab is not null) { _tabs.TabPages.Remove(tab); tab.Dispose(); }
            else view?.Dispose();
            UpdateUnreadBadges();
            await _catalog.StopAsync(network);
            throw;
        }
    }

    private void UpdateUnreadBadges()
    {
        if (_exiting) return;
        var hasUnread = false;
        foreach (var item in _running.Values)
        {
            var count = item.View.UnreadCount;
            _tabs.SetIcon(item.Tab, count > 0 ? UiIcon.NetworkUnread : UiIcon.Network);
            item.Tab.ToolTipText = count > 0 ? $"{count} tin nhắn chưa đọc" : "";
            hasUnread |= count > 0;
        }
        var icon = AppIcon.ForUnread(hasUnread);
        if (!ReferenceEquals(_tray.Icon, icon)) _tray.Icon = icon;
        UpdateReminderTimer();
    }

    private void ShowNotification(Guid networkId, Guid conversationId, Guid? messageId, string title, string body)
    {
        if (_exiting) return;
        _notificationTarget = (networkId, conversationId, messageId);
        _tray.ShowBalloonTip(3000, title, body, ToolTipIcon.Info);
    }

    private void UpdateReminderTimer(bool restart = false)
    {
        var preferences = _catalog.UiPreferences;
        if (_exiting || !preferences.RemindUnreadMessages || !_running.Values.Any(item => item.View.ReminderUnreadCount > 0))
        {
            _unreadReminder.Stop();
            return;
        }

        var interval = preferences.UnreadReminderMinutes * 60_000;
        if (restart || _unreadReminder.Interval != interval)
        {
            _unreadReminder.Stop();
            _unreadReminder.Interval = interval;
        }
        _unreadReminder.Start();
    }

    private void ShowUnreadReminder()
    {
        if (_exiting || !_catalog.UiPreferences.RemindUnreadMessages) return;
        var count = 0;
        (Guid Network, Guid Conversation)? target = null;
        foreach (var entry in _running)
        {
            if (entry.Value.View.GetUnreadReminder() is not { } reminder) continue;
            target ??= (entry.Key, reminder.ConversationId);
            count += reminder.Count;
        }

        if (target is { } unread)
            ShowNotification(unread.Network, unread.Conversation, null, "LanChat · Nhắc tin chưa đọc", $"Bạn còn {count} tin nhắn chưa đọc.");
    }

    private Task MarkSelectedReadAsync() => _tabs.SelectedTab?.Tag is Guid id && _running.TryGetValue(id, out var item)
        ? item.View.MarkReadOnActivationAsync() : Task.CompletedTask;

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _exiting || _installing) return;
        _busy = true;
        UpdateActionState();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { UiInteraction.Warn(this, ex.Message); }
        finally { _busy = false; if (!IsDisposed) UpdateActionState(); }
        if (_restartAsAdministrator)
        {
            _restartAsAdministrator = false;
            ExitApplication();
            RestartAsAdministrator = _exiting;
            if (_exiting) return;
        }
        if (_installRequested) { _installRequested = false; await InstallUpdateAsync(); }
        await OpenPendingNotificationAsync();
    }

    private Task ShowAboutAsync()
    {
        using var dialog = new AboutForm(_updates, () => _scan is null && !_running.Values.Any(item => item.View.HasActiveWork), ShowUpdateSettings);
        UiInteraction.ShowDialog(dialog, this);
        _installRequested = dialog.InstallRequested;
        return Task.CompletedTask;
    }

    private CancellationTokenSource? _scan;
    private async Task ScanAsync()
    {
        if (_running.Count == 0) return;
        using var scan = new CancellationTokenSource();
        _scan = scan;
        _feedback.Text = $"Đang tìm máy trên {_running.Count} mạng…";
        _scanButton.Text = "Dừng quét";
        _scanButton.Icon = UiIcon.Stop;
        UpdateActionState();
        try
        {
            var scanners = _running.Values.Select(x => x.Network.Services.GetRequiredService<ILanScanner>()).ToArray();
            int count;
            if (_catalog.UiPreferences.SaveSystemResources)
            {
                count = 0;
                foreach (var scanner in scanners) count += (await scanner.ScanAsync(scan.Token)).Count;
            }
            else
            {
                var found = await Task.WhenAll(scanners.Select(scanner => scanner.ScanAsync(scan.Token)));
                count = found.Sum(x => x.Count);
            }
            if (!IsDisposed)
            {
                var reason = _running.Values.Select(x => x.Network.Services.GetRequiredService<NetworkHealth>().DiscoveryIssue)
                    .FirstOrDefault(x => x is not null);
                _feedback.Text = count == 0 ? reason ?? "Chưa tìm thấy máy. Kiểm tra ID mạng và tường lửa trên cả hai máy."
                    : $"Tìm thấy {count} máy trên {scanners.Length} mạng.";
            }
        }
        catch (OperationCanceledException) { if (!IsDisposed) _feedback.Text = "Đã dừng quét."; }
        catch (Exception ex) { if (!IsDisposed) _feedback.Text = UiText.HideIpAddresses(ex.Message); }
        finally
        {
            _scan = null;
            if (!_scanButton.IsDisposed) { _scanButton.Text = "Quét tất cả"; _scanButton.Icon = UiIcon.Scan; }
            if (!IsDisposed) UpdateActionState();
        }
    }

    private async Task ShowGettingStartedAsync()
    {
        using var guide = new GettingStartedForm(() => _catalog.Profile.Name, () => _running.Count > 0,
            SettingsAsync, ManageAsync, ScanAsync);
        UiInteraction.ShowDialog(guide, this);
        await _catalog.SaveUiPreferencesAsync(_catalog.UiPreferences with { OnboardingCompleted = true });
    }

    private Task SettingsAsync()
    {
        var profile = _catalog.Profile;
        var runAsAdministrator = _startup.RunAsAdministrator;
        using var dialog = new SettingsForm(profile.Name, profile.Department, _startup.Enabled, async (name, department, startup) =>
        {
            if (_startup.NeedsUpdate(startup)) _startup.SetEnabled(startup);
            await _catalog.SaveProfileAsync(name, department);
            foreach (var network in _running.Values)
            {
                await _catalog.ApplyProfileAsync(network.Network.Services.GetRequiredService<IChatStore>());
                await network.View.UpdateProfileAsync();
            }
        }, ShowUpdateSettings, _catalog.UiPreferences, async preferences =>
        {
            await _catalog.SaveUiPreferencesAsync(preferences);
            UpdateReminderTimer(restart: true);
            await ApplyConfiguredThemeAsync();
            foreach (var network in _running.Values) network.View.ApplyPreferences(preferences);
        }, runAsAdministrator, enabled =>
        {
            if (enabled != runAsAdministrator) _startup.RunAsAdministrator = enabled;
            _restartAsAdministrator = enabled && !Program.IsAdministrator();
        });
        UiInteraction.ShowDialog(dialog, this);
        return Task.CompletedTask;
    }

    private void WindowsAppearanceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)(async () => await ApplyConfiguredThemeAsync())); }
        catch (InvalidOperationException) { }
    }

    private void ReleaseAppearance(object? sender, EventArgs e)
    {
        Disposed -= ReleaseAppearance;
        _unreadReminder.Dispose();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= WindowsAppearanceChanged;
        _appearanceLifetime.Cancel();
        _appearanceLifetime.Dispose();
    }

    private async Task ApplyConfiguredThemeAsync()
    {
        if (IsDisposed || Disposing) return;
        var request = ++_appearanceRequest;
        var ct = _appearanceLifetime.Token;
        try
        {
            var mode = await Theme.ResolveModeAsync(_catalog.UiPreferences.Theme, ct);
            if (!IsDisposed && !ct.IsCancellationRequested && request == _appearanceRequest) Theme.SetMode(mode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void ShowUpdateSettings()
    {
        var choices = _catalog.Entries.Select(entry => new UpdateNetworkChoice(entry.Id, entry.Name,
            _running.TryGetValue(entry.Id, out var running) ? running.Network.Services : null,
            _running.TryGetValue(entry.Id, out var connected) ? connected.Network.Port : NodeOptions.DefaultServicePort)).ToArray();
        using var form = new UpdateSettingsForm(_updates, choices, _tabs.SelectedTab?.Tag as Guid?);
        UiInteraction.ShowDialog(form, this);
    }

    private async Task RestoreDefaultNetworkAsync()
    {
        var entry = _catalog.Entries.FirstOrDefault(_catalog.IsDefault) ?? await _catalog.RestoreDefaultAsync();
        await OpenNetworkAsync(entry);
        _tabs.SelectedTab = _running[entry.Id].Tab;
        _restoreDefaultButton.Visible = false;
        _feedback.Text = "Đã mở mạng mặc định.";
    }

    private Task ManageAsync()
    {
        using var manager = new NetworkManagerForm(_catalog, OpenNetworkAsync, RemoveNetworkAsync, NetworkDetails, IsConnected, RestoreDefaultNetworkAsync,
            () => _tabs.SelectedTab?.Tag as Guid?, id => { if (_running.TryGetValue(id, out var network)) _tabs.SelectedTab = network.Tab; });
        UiInteraction.ShowDialog(manager, this);
        if (_running.Count == 0 || _feedback.Text == NoNetworkMessage)
            _feedback.Text = _running.Count == 0 ? NoNetworkMessage : "";
        foreach (var entry in _catalog.Entries)
            if (_running.TryGetValue(entry.Id, out var item)) item.Tab.Text = entry.Name;
        _restoreDefaultButton.Visible = _catalog.CanRestoreDefaultNetwork && !_catalog.Entries.Any(_catalog.IsDefault);
        _manageNetworksButton.Visible = _running.Count == 0;
        return Task.CompletedTask;
    }

    private bool IsConnected(Guid id) => _running.ContainsKey(id);

    private void NetworkDetails(NetworkEntry entry, Form owner)
    {
        if (!_running.TryGetValue(entry.Id, out var item)) return;
        using var dialog = ActivatorUtilities.CreateInstance<NetworkForm>(item.Network.Services);
        dialog.ShowDialog(owner);
    }

    private async Task RemoveNetworkAsync(NetworkEntry entry)
    {
        EnsureNetworkCanClose(entry);
        if (!_updates.Service.Packages.TryPauseNetwork(entry.Id))
            throw new InvalidOperationException("Mạng đang cung cấp bản cập nhật. Hãy chờ tải xong rồi gỡ mạng.");
        try { await _catalog.RemoveAsync(entry); await StopNetworkAsync(entry); }
        catch { _updates.Service.Packages.ResumeNetwork(entry.Id); throw; }
    }

    private void EnsureNetworkCanClose(NetworkEntry entry)
    {
        if ((_updates.State.Busy && _updates.Service.Settings.Kind == UpdateSourceKind.Lan && _updates.Service.Settings.NetworkId == entry.Id) ||
            _updates.Service.Packages.ActiveTransfersFor(entry.Id) > 0)
            throw new InvalidOperationException("Mạng này đang tải hoặc cung cấp bản cập nhật. Hãy hoàn tất hoặc hủy tác vụ trước khi gỡ mạng.");
        if (!_running.TryGetValue(entry.Id, out var item)) return;
        if (item.View.HasActiveWork) throw new InvalidOperationException("Hãy hoàn tất hoặc hủy tác vụ trên mạng này trước khi gỡ mạng.");
        if (item.View.HasUnsentDrafts && !UiInteraction.Discard(this,
            $"Mạng {entry.Name} còn bản nháp chưa gửi hoặc thay đổi thành viên chưa lưu. Bỏ các bản nháp và gỡ mạng?"))
            throw new OperationCanceledException();
    }

    private async Task StopNetworkAsync(NetworkEntry entry)
    {
        if (!_running.TryGetValue(entry.Id, out var item)) return;
        _running.Remove(entry.Id);
        _manageNetworksButton.Visible = _running.Count == 0;
        _updateNetworks.TryRemove(entry.Id, out _);
        item.View.Close(); _tabs.TabPages.Remove(item.Tab); item.Tab.Dispose();
        UpdateUnreadBadges();
        await _catalog.StopAsync(item.Network);
    }

    private async void ClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (e.CloseReason == CloseReason.UserClosing && !_exitRequested && !_exiting)
        {
            if (!_busy || _scan is not null) Hide();
            return;
        }
        if (_exiting) return;
        if (e.CloseReason == CloseReason.UserClosing && (_busy || _installing || _updates.Service.Packages.Preparing || _updates.Service.Packages.ActiveTransfers > 0 || _running.Values.Any(item => item.View.HasActiveWork)))
        {
            _exitRequested = false;
            RestoreWindow();
            UiInteraction.Warn(this, "Hãy hoàn tất hoặc hủy tác vụ đang chạy, đóng hộp thoại rồi chọn Thoát LanChat.");
            return;
        }
        if (e.CloseReason == CloseReason.UserClosing && PendingInstaller is null && _running.Values.Any(item => item.View.HasUnsentDrafts) &&
            !UiInteraction.Discard(this, "Bạn còn bản nháp chưa gửi hoặc thay đổi thành viên chưa lưu. Bỏ các bản nháp và thoát LanChat?"))
        { _exitRequested = false; return; }
        _exiting = true;
        _unreadReminder.Stop();
        _updateNetworks.Clear();
        UpdateActionState();
        _scan?.Cancel();
        foreach (var item in _running.Values) item.View.Close();
        try { await Task.WhenAll(_running.Values.Select(x => _catalog.StopAsync(x.Network)).Append(_updates.StopAsync())); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError(ex.ToString()); }
        finally
        {
            _running.Clear();
            _tray.Dispose();
            _closed = true;
            Close();
        }
    }
}
