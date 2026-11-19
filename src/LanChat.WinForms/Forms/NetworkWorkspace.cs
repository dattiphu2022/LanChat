using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace LanChat.WinForms.Forms;

internal sealed partial class NetworkWorkspace : DpiForm
{
    private readonly NetworkCatalog _catalog;
    private readonly WindowsStartup _startup;
    private readonly IconTabControl _tabs = new(UiIcon.Network, UiIcon.NetworkUnread)
        { Dock = DockStyle.Fill, ShowToolTips = true };
    private readonly Dictionary<Guid, (RunningNetwork Network, MainForm View, TabPage Tab)> _running = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, IServiceProvider> _updateNetworks = new();
    private readonly IconButton _scanButton = Theme.Secondary("Quét tất cả", UiIcon.Scan);
    private readonly Button _restoreDefaultButton;
    private readonly Label _feedback = Theme.Label("");
    private readonly NotifyIcon _tray = new() { Icon = AppIcon.Value, Text = "LanChat", Visible = true };
    private readonly List<Button> _actions = [];
    private readonly List<Button> _networkActions = [];
    private bool _busy, _exiting, _closed, _exitRequested;
    private FormWindowState _restoreState;
    private (Guid NetworkId, Guid ConversationId)? _notificationTarget;
    private (Guid NetworkId, Guid ConversationId)? _pendingNotification;

    public NetworkWorkspace(NetworkCatalog catalog, WindowsStartup startup, UpdateService updates)
    {
        _catalog = catalog; _startup = startup;
        _updates = new UpdateCoordinator(updates);
        updates.ResolveNetwork = id => _updateNetworks.TryGetValue(id, out var services) ? services : null;
        Icon = AppIcon.Value;
        Text = "LanChat";
        ClientSize = new Size(1260, 840); MinimumSize = new Size(660, 450);
        StartPosition = FormStartPosition.CenterScreen;
        var root = Theme.Stack(4);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = Theme.Toolbar();
        actions.Padding = new Padding(8, 0, 0, 0);
        _feedback.UseMnemonic = false;
        actions.SizeChanged += (_, _) => _feedback.MaximumSize = new Size(Math.Max(1, actions.ClientSize.Width - 16), 0);
        Button AddAction(string label, UiIcon icon, Func<Task> action, bool changesNetworks = false)
        {
            var button = Theme.Secondary(label, icon);
            button.Click += async (_, _) => await RunAsync(action);
            actions.Controls.Add(button);
            _actions.Add(button);
            if (changesNetworks) _networkActions.Add(button);
            return button;
        }
        _scanButton.Click += async (_, _) => { if (_scan is not null) _scan.Cancel(); else if (!_busy && !_installing) await ScanAsync(); };
        actions.Controls.Add(_scanButton);
        AddAction("Mạng chat", UiIcon.Network, ManageAsync, changesNetworks: true);
        _restoreDefaultButton = AddAction("Khôi phục mạng mặc định", UiIcon.Restore, RestoreDefaultNetworkAsync, changesNetworks: true);
        _restoreDefaultButton.Visible = _catalog.CanRestoreDefaultNetwork;
        AddAction("Cài đặt", UiIcon.Settings, SettingsAsync);
        AddAction("Giới thiệu", UiIcon.Info, ShowAboutAsync);
        root.Controls.Add(actions, 0, 0); root.Controls.Add(_feedback, 0, 1);
        root.Controls.Add(CreateUpdateBar(), 0, 2); root.Controls.Add(_tabs, 0, 3);
        Controls.Add(root);
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Mở LanChat", UiIcons.ForMenu(UiIcon.Open), (_, _) => RestoreWindow());
        trayMenu.Items.Add("Thoát LanChat", UiIcons.ForMenu(UiIcon.Exit), (_, _) => { _exitRequested = true; Close(); });
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
        FormClosed += (_, _) => { trayMenu.Dispose(); _updates.Dispose(); };
        FormClosing += ClosingAsync;
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
        await item.View.OpenNotificationAsync(target.ConversationId);
    }

    private void UpdateActionState()
    {
        foreach (var button in _actions)
            button.Enabled = !_busy && !_installing && !_exiting && (_scan is null || !_networkActions.Contains(button));
        _scanButton.Enabled = !_exiting && !_installing && (_scan is not null || (!_busy && _running.Count > 0));
        _tabs.Enabled = !_installing && !_exiting;
    }

    internal void ExitApplication() { _exitRequested = true; Close(); }

    public async Task StartAsync()
    {
        _busy = true;
        UpdateActionState();
        try { await LoadNetworksAsync(); }
        finally { _busy = false; UpdateActionState(); }
        _ = _updates.CheckAsync(manual: false);
    }

    private async Task LoadNetworksAsync()
    {
        await _catalog.LoadAsync();
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
            view.EmbedInWorkspace((conversationId, title, body) => ShowNotification(entry.Id, conversationId, title, body),
                () => Visible && WindowState != FormWindowState.Minimized && _tabs.SelectedTab == tab);
            view.Dock = DockStyle.Fill;
            tab.Controls.Add(view); _tabs.TabPages.Add(tab);
            _running.Add(entry.Id, (network, view, tab));
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
    }

    private void ShowNotification(Guid networkId, Guid conversationId, string title, string body)
    {
        if (_exiting) return;
        _notificationTarget = (networkId, conversationId);
        _tray.ShowBalloonTip(3000, title, body, ToolTipIcon.Info);
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
            var tasks = _running.Values.Select(x => x.Network.Services.GetRequiredService<ILanScanner>().ScanAsync(scan.Token)).ToArray();
            var found = await Task.WhenAll(tasks);
            if (!IsDisposed)
            {
                var count = found.Sum(x => x.Count);
                var reason = _running.Values.Select(x => x.Network.Services.GetRequiredService<NetworkHealth>().DiscoveryIssue)
                    .FirstOrDefault(x => x is not null);
                _feedback.Text = count == 0 ? reason ?? "Chưa tìm thấy máy. Kiểm tra ID mạng và tường lửa trên cả hai máy."
                    : $"Tìm thấy {count} máy trên {tasks.Length} mạng.";
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

    private Task SettingsAsync()
    {
        var profile = _catalog.Profile;
        using var dialog = new SettingsForm(profile.Name, profile.Department, _startup.Enabled, async (name, department, startup) =>
        {
            if (_startup.NeedsUpdate(startup)) _startup.SetEnabled(startup);
            await _catalog.SaveProfileAsync(name, department);
            foreach (var network in _running.Values)
            {
                await _catalog.ApplyProfileAsync(network.Network.Services.GetRequiredService<IChatStore>());
                await network.View.UpdateProfileAsync();
            }
        }, ShowUpdateSettings);
        UiInteraction.ShowDialog(dialog, this);
        return Task.CompletedTask;
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
