using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace LanChat.WinForms.Forms;

internal sealed partial class NetworkWorkspace : DpiForm
{
    private readonly NetworkCatalog _catalog;
    private readonly WindowsStartup _startup;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, ShowToolTips = true };
    private readonly ImageList _tabIcons = new() { ColorDepth = ColorDepth.Depth32Bit };
    private readonly Dictionary<Guid, (RunningNetwork Network, MainForm View, TabPage Tab)> _running = new();
    private readonly Button _scanButton = Theme.Secondary("Quét tất cả");
    private readonly Button _restoreDefaultButton;
    private readonly Label _feedback = Theme.Label("");
    private readonly NotifyIcon _tray = new() { Icon = AppIcon.Value, Text = "LanChat", Visible = true };
    private bool _busy, _exiting, _closed, _exitRequested;
    private FormWindowState _restoreState;
    private (Guid NetworkId, Guid ConversationId)? _notificationTarget;

    public NetworkWorkspace(NetworkCatalog catalog, WindowsStartup startup, UpdateService updates)
    {
        _catalog = catalog; _startup = startup;
        _updates = updates;
        Icon = AppIcon.Value;
        Text = "LanChat";
        ClientSize = new Size(1260, 840); MinimumSize = new Size(660, 450);
        StartPosition = FormStartPosition.CenterScreen;
        Load += (_, _) => UpdateTabIcons();
        DpiChanged += (_, _) => UpdateTabIcons();
        var root = Theme.Stack(4);
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = Theme.Toolbar();
        actions.Padding = new Padding(8, 0, 0, 0);
        _feedback.AutoSize = false; _feedback.AutoEllipsis = true;
        Button AddAction(string label, Func<Task> action)
        {
            var button = Theme.Secondary(label);
            button.Click += async (_, _) => await RunAsync(action);
            actions.Controls.Add(button);
            return button;
        }
        _scanButton.Click += async (_, _) => { if (_scan is not null) _scan.Cancel(); else await RunAsync(ScanAsync); };
        actions.Controls.Add(_scanButton);
        AddAction("Mạng chat", ManageAsync);
        _restoreDefaultButton = AddAction("Khôi phục mạng mặc định", RestoreDefaultNetworkAsync);
        _restoreDefaultButton.Visible = _catalog.CanRestoreDefaultNetwork;
        AddAction("Cài đặt", SettingsAsync);
        AddAction("Giới thiệu", ShowAboutAsync);
        root.Controls.Add(actions, 0, 0); root.Controls.Add(_feedback, 0, 1);
        root.Controls.Add(CreateUpdateBar(), 0, 2); root.Controls.Add(_tabs, 0, 3);
        Controls.Add(root);
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Mở LanChat", null, (_, _) => RestoreWindow());
        trayMenu.Items.Add("Thoát LanChat", null, (_, _) => { _exitRequested = true; Close(); });
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => RestoreWindow();
        _tray.BalloonTipClicked += async (_, _) =>
        {
            RestoreWindow();
            if (_exiting || _notificationTarget is not { } target || !_running.TryGetValue(target.NetworkId, out var item)) return;
            _tabs.SelectedTab = item.Tab;
            await item.View.OpenNotificationAsync(target.ConversationId);
        };
        Activated += async (_, _) => await MarkSelectedReadAsync();
        _tabs.SelectedIndexChanged += async (_, _) => await MarkSelectedReadAsync();
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) Hide();
            else _restoreState = WindowState;
        };
        FormClosed += (_, _) => { trayMenu.Dispose(); _tabIcons.Dispose(); _updateLifetime.Dispose(); };
        FormClosing += ClosingAsync;
    }

    private void UpdateTabIcons()
    {
        var size = new Size(LogicalToDeviceUnits(12), LogicalToDeviceUnits(12));
        if (_tabIcons.Images.Count > 0 && _tabIcons.ImageSize == size) return;
        _tabIcons.Images.Clear();
        _tabIcons.ImageSize = size;
        var inset = LogicalToDeviceUnits(1);
        using var dot = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(dot))
            UnreadBadge.PaintDot(graphics, Rectangle.Inflate(new Rectangle(Point.Empty, size), -inset, -inset));
        _tabIcons.Images.Add(dot);
        _tabs.ImageList = _tabIcons;
        _ = _tabIcons.Handle;
    }

    private void RestoreWindow()
    {
        if (_exiting) return;
        Show(); WindowState = _restoreState; Activate();
    }

    internal void ExitApplication() { _exitRequested = true; Close(); }

    public async Task StartAsync()
    {
        _ = CheckForUpdatesAsync(manual: false);
        _busy = true;
        try { await LoadNetworksAsync(); }
        finally { _busy = false; }
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
            tab = new TabPage(entry.Name) { Tag = entry.Id };
            view.EmbedInWorkspace((conversationId, title, body) => ShowNotification(entry.Id, conversationId, title, body),
                () => Visible && WindowState != FormWindowState.Minimized && _tabs.SelectedTab == tab);
            view.Dock = DockStyle.Fill;
            tab.Controls.Add(view); _tabs.TabPages.Add(tab);
            _running.Add(entry.Id, (network, view, tab));
            view.UnreadCountChanged += UpdateUnreadBadges;
            view.Show();
            await view.StartViewAsync();
        }
        catch
        {
            _running.Remove(entry.Id);
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
            var image = count > 0 ? 0 : -1;
            if (item.Tab.ImageIndex != image) item.Tab.ImageIndex = image;
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
        if (_busy || _exiting) return;
        _busy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, UiText.HideIpAddresses(ex.Message), "LanChat", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _busy = false; }
    }

    private Task ShowAboutAsync()
    {
        using var dialog = new AboutForm(_checkMessage, () => CheckForUpdatesAsync(manual: true));
        dialog.ShowDialog(this);
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
        finally { _scan = null; if (!_scanButton.IsDisposed) _scanButton.Text = "Quét tất cả"; }
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
        });
        dialog.ShowDialog(this);
        return Task.CompletedTask;
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
        using var manager = new NetworkManagerForm(_catalog, OpenNetworkAsync, RemoveNetworkAsync, StopNetworkAsync, NetworkDetails, IsConnected, RestoreDefaultNetworkAsync,
            () => _tabs.SelectedTab?.Tag as Guid?, id => { if (_running.TryGetValue(id, out var network)) _tabs.SelectedTab = network.Tab; });
        manager.ShowDialog(this);
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
        await _catalog.RemoveAsync(entry);
        await StopNetworkAsync(entry);
    }

    private async Task StopNetworkAsync(NetworkEntry entry)
    {
        if (!_running.TryGetValue(entry.Id, out var item)) return;
        _running.Remove(entry.Id);
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
        if (_busy && _scan is null && e.CloseReason == CloseReason.UserClosing) { _exitRequested = false; return; }
        _exiting = true;
        _scan?.Cancel();
        foreach (var item in _running.Values) item.View.Close();
        try { await Task.WhenAll(_running.Values.Select(x => _catalog.StopAsync(x.Network)).Append(StopUpdateOperationsAsync())); }
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
