using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace LanChat.WinForms.Forms;

internal sealed class NetworkWorkspace : Form
{
    private readonly NetworkCatalog _catalog;
    private readonly WindowsStartup _startup;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<Guid, (RunningNetwork Network, MainForm View, TabPage Tab)> _running = new();
    private readonly Button _scanButton = Theme.Secondary("Quét tất cả");
    private readonly Label _feedback = Theme.Label("");
    private readonly NotifyIcon _tray = new() { Icon = AppIcon.Value, Text = "LanChat", Visible = true };
    private bool _busy, _exiting, _closed, _exitRequested;
    private FormWindowState _restoreState;

    public NetworkWorkspace(NetworkCatalog catalog, WindowsStartup startup)
    {
        _catalog = catalog; _startup = startup;
        Icon = AppIcon.Value;
        Text = "LanChat";
        ClientSize = new Size(1260, 840); MinimumSize = new Size(900, 660);
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;

        var root = Theme.Stack(3);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(8, 0, 0, 0) };
        void Button(string label, Func<Task> action)
        {
            var button = Theme.Secondary(label);
            button.Click += async (_, _) => await RunAsync(action);
            actions.Controls.Add(button);
        }
        _scanButton.Click += async (_, _) => { if (_scan is not null) _scan.Cancel(); else await RunAsync(ScanAsync); };
        actions.Controls.Add(_scanButton);
        Button("Mạng chat", ManageAsync);
        Button("Khôi phục mạng mặc định", RestoreDefaultNetworkAsync);
        Button("Cài đặt", SettingsAsync);
        root.Controls.Add(actions, 0, 0); root.Controls.Add(_feedback, 0, 1); root.Controls.Add(_tabs, 0, 2);
        Controls.Add(root);
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Mở LanChat", null, (_, _) => RestoreWindow());
        trayMenu.Items.Add("Thoát LanChat", null, (_, _) => { _exitRequested = true; Close(); });
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => RestoreWindow();
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) Hide();
            else _restoreState = WindowState;
        };
        FormClosed += (_, _) => trayMenu.Dispose();
        FormClosing += ClosingAsync;
    }

    private void RestoreWindow()
    {
        if (_exiting) return;
        Show(); WindowState = _restoreState; Activate();
    }

    internal void ExitApplication() { _exitRequested = true; Close(); }

    public async Task StartAsync()
    {
        _busy = true;
        try { await LoadNetworksAsync(); }
        finally { _busy = false; }
    }

    private async Task LoadNetworksAsync()
    {
        await _catalog.LoadAsync();
        var failures = new List<string>();
        if (_catalog.DefaultNetworkError is string defaultError) failures.Add(defaultError);
        foreach (var entry in _catalog.Entries.OrderByDescending(e => e.Primary))
        {
            try { await OpenNetworkAsync(entry, allowUpgrade: true); }
            catch (Exception ex) { failures.Add(entry.Name + " · " + ex.Message); }
        }
        if (_catalog.DefaultNetwork is { } selectedDefault && _running.TryGetValue(selectedDefault.Id, out var defaultNetwork))
            _tabs.SelectedTab = defaultNetwork.Tab;
        _feedback.Text = failures.Count > 0 ? string.Join("  |  ", failures) :
            _running.Count == 0 ? "Chọn Khôi phục mạng mặc định để tiếp tục chat." : "";
    }

    private async Task OpenNetworkAsync(NetworkEntry entry, bool allowUpgrade = false)
    {
        if (_running.ContainsKey(entry.Id)) return;
        var network = await _catalog.StartAsync(entry, allowUpgrade, this);
        TabPage? tab = null;
        MainForm? view = null;
        try
        {
            view = ActivatorUtilities.CreateInstance<MainForm>(network.Services);
            view.EmbedInWorkspace((title, body) => _tray.ShowBalloonTip(3000, title, body, ToolTipIcon.Info));
            tab = new TabPage(entry.Name) { Tag = entry.Id };
            view.Dock = DockStyle.Fill;
            tab.Controls.Add(view); _tabs.TabPages.Add(tab);
            _running.Add(entry.Id, (network, view, tab));
            view.Show();
        }
        catch
        {
            _running.Remove(entry.Id);
            if (tab is not null) { _tabs.TabPages.Remove(tab); tab.Dispose(); }
            else view?.Dispose();
            await _catalog.StopAsync(network);
            throw;
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _exiting) return;
        _busy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "LanChat", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _busy = false; }
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
        var profile = _catalog.Profile ?? new AppProfile(Environment.UserName, "");
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
        try { await Task.WhenAll(_running.Values.Select(x => _catalog.StopAsync(x.Network))); }
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
