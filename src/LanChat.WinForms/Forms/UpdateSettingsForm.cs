using System.Net;
using LanChat.Application;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed record UpdateNetworkChoice(Guid Id, string Name, IServiceProvider? Services, int Port)
{
    public override string ToString() => Name + (Services is null ? " · Chưa kết nối" : "");
}
internal sealed class UpdateSettingsForm : DpiForm
{
    private readonly UpdateCoordinator _updates;
    private readonly UpdateNetworkChoice[] _networks;
    private readonly ComboBox _source = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Nguồn cập nhật" };
    private readonly ComboBox _network = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Mạng chứa máy nguồn" };
    private readonly ComboBox _peer = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Máy nguồn" };
    private readonly CheckBox _manual = new() { AutoSize = true, Text = "Nhập IP và cổng (nâng cao)" };
    private readonly PromptTextBox _ip = new() { Dock = DockStyle.Top, PlaceholderText = "IPv4 máy nguồn, ví dụ 192.168.1.20",
        AccessibleName = "IP máy nguồn" };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 54546, Dock = DockStyle.Top, AccessibleName = "Cổng máy nguồn" };
    private readonly CheckBox _share = new() { AutoSize = true, Text = "Máy này cung cấp cập nhật qua LAN" };
    private readonly CheckedListBox _allowed = new() { Dock = DockStyle.Top, Height = 100, CheckOnClick = true, IntegralHeight = false, AccessibleName = "Mạng được phép nhận cập nhật" };
    private readonly Label _packageInfo = Theme.Label("Chưa chọn bộ cài.", 9);
    private readonly Label _status = Theme.Label("", 9);
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Maximum = 1000, Visible = false, Height = 16 };
    private readonly Button _save = Theme.Button("Lưu", UiIcon.Save);
    private readonly Button _verify = Theme.Secondary("Kiểm tra kết nối", UiIcon.Network);
    private readonly Button _pick = Theme.Secondary("Chọn bộ cài EXE…", UiIcon.Folder);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Panel _body = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly TableLayoutPanel _lan = Theme.Stack(8), _local = Theme.Stack(3), _sharing = Theme.Stack(2);
    private UpdatePackage? _package;
    private PeerEndpoint? _verified;
    private string _verifiedName = "";
    private CancellationTokenSource? _operation;
    private bool _closed, _dirty, _populatingPeers;
    private int _peerGeneration;

    public UpdateSettingsForm(UpdateCoordinator updates, UpdateNetworkChoice[] networks, Guid? selectedNetwork)
    {
        _updates = updates; _networks = networks; var settings = updates.Service.Settings; _package = settings.Package;
        Text = "Cài đặt cập nhật — LanChat"; Icon = AppIcon.Value; ClientSize = new Size(680, 660);
        MinimumSize = new Size(500, 390); StartPosition = FormStartPosition.CenterParent; MinimizeBox = MaximizeBox = false; ShowInTaskbar = false;
        var root = Theme.Stack(4); root.Padding = new Padding(12);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var content = Theme.Stack(7); content.Dock = DockStyle.Top; content.AutoSize = true; content.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        for (var i = 0; i < 7; i++) content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (var i = 0; i < 8; i++) _lan.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (var i = 0; i < 3; i++) _local.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (var i = 0; i < 2; i++) _sharing.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (var group in new[] { _lan, _local, _sharing }) { group.Dock = DockStyle.Top; group.AutoSize = true; group.AutoSizeMode = AutoSizeMode.GrowAndShrink; group.Margin = new Padding(0, 8, 0, 8); }
        _source.Items.AddRange(["Online — GitHub", "Máy trong LAN", "Bộ cài trên máy này"]);
        _source.SelectedIndex = Enum.IsDefined(typeof(UpdateSourceKind), settings.Kind) ? (int)settings.Kind : -1;
        _network.Items.AddRange(networks); _allowed.Items.AddRange(networks);
        var selected = Array.FindIndex(networks, n => n.Id == (settings.NetworkId != Guid.Empty ? settings.NetworkId : selectedNetwork));
        _network.SelectedIndex = selected >= 0 ? selected : networks.Length > 0 ? 0 : -1;
        for (var i = 0; i < networks.Length; i++) _allowed.SetItemChecked(i, settings.Networks?.Contains(networks[i].Id) == true || (!settings.Sharing && networks[i].Id == selectedNetwork));
        _share.Checked = settings.Sharing; _ip.Text = settings.Address; _port.Value = settings.Port is >= 1 and <= 65535 ? settings.Port : 54546;
        _manual.Checked = false;
        _lan.Controls.Add(Theme.Label("Mạng chứa máy nguồn"), 0, 0); _lan.Controls.Add(_network, 0, 1); _lan.Controls.Add(_peer, 0, 2);
        var portLabel = Theme.Label("Cổng dịch vụ LanChat trên máy nguồn");
        _lan.Controls.Add(_manual, 0, 3); _lan.Controls.Add(_ip, 0, 4); _lan.Controls.Add(portLabel, 0, 5);
        _lan.Controls.Add(_port, 0, 6); _lan.Controls.Add(_verify, 0, 7);
        var explanation = Theme.Label("LAN dùng bộ cài do máy bạn chọn cung cấp. Chỉ nhận từ máy cùng mạng chat; không tự chuyển sang GitHub khi LAN lỗi.", 9);
        UiInteraction.Wrap(explanation, content); UiInteraction.Wrap(_packageInfo, content); UiInteraction.Wrap(_status, root);
        _local.Controls.Add(_pick, 0, 0); _local.Controls.Add(_packageInfo, 0, 1);
        var localHint = Theme.Label("Bản gốc được giữ nguyên; ứng dụng tạo bản chia sẻ riêng. Chọn Bộ cài trên máy này để tự cài gói đã chọn.", 9);
        UiInteraction.Wrap(localHint, content); _local.Controls.Add(localHint, 0, 2);
        _sharing.Controls.Add(Theme.Label("Các mạng được phép nhận bộ cài"), 0, 0); _sharing.Controls.Add(_allowed, 0, 1);
        content.Controls.Add(Theme.Label("Nguồn kiểm tra cập nhật", 11, true), 0, 0); content.Controls.Add(_source, 0, 1);
        content.Controls.Add(_lan, 0, 2); content.Controls.Add(explanation, 0, 3); content.Controls.Add(_share, 0, 4);
        content.Controls.Add(_sharing, 0, 5); content.Controls.Add(_local, 0, 6); _body.Controls.Add(content);
        void FitContent() => content.Width = Math.Max(1, _body.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
        _body.SizeChanged += (_, _) => FitContent(); FitContent();
        var buttons = Theme.Toolbar(); var cancelOperation = Theme.Secondary("Hủy tác vụ", UiIcon.Close); cancelOperation.Visible = false;
        cancelOperation.Click += (_, _) => { _operation?.Cancel(); _status.Text = "Đang hủy…"; };
        buttons.Controls.AddRange([_save, cancelOperation, UiInteraction.CloseButton(this, "Hủy")]); AcceptButton = _save;
        root.Controls.Add(_body, 0, 0); root.Controls.Add(_status, 0, 1); root.Controls.Add(_progress, 0, 2); root.Controls.Add(buttons, 0, 3); Controls.Add(root);
        void Refresh()
        {
            if (_closed) return;
            var busy = _operation is not null;
            _lan.Visible = _source.SelectedIndex == (int)UpdateSourceKind.Lan;
            _local.Visible = _source.SelectedIndex == (int)UpdateSourceKind.Local || _share.Checked;
            _sharing.Visible = _share.Checked; _ip.Visible = portLabel.Visible = _port.Visible = _manual.Checked;
            _peer.Enabled = !_manual.Checked; _save.Enabled = !busy && !updates.State.Busy && _source.SelectedIndex >= 0;
            _body.Enabled = !busy; cancelOperation.Visible = _progress.Visible = busy;
            const string busyReason = "Đang có tác vụ cập nhật. Hoàn tất hoặc hủy tác vụ đó trước khi lưu nguồn mới.";
            if (updates.State.Busy && !busy) _status.Text = busyReason;
            else if (_status.Text == busyReason) _status.Text = "";
            _packageInfo.Text = _package is null ? "Chưa chọn bộ cài." : $"LanChat {_package.Version} · {_package.Size / 1048576d:N1} MB · Đã kiểm tra và tạo bản cục bộ.";
        }
        void InvalidatePeer() { _verified = null; if (!_populatingPeers) _dirty = true; }
        _source.SelectedIndexChanged += (_, _) => { InvalidatePeer(); Refresh(); };
        _share.CheckedChanged += (_, _) => { _dirty = true; Refresh(); };
        _allowed.ItemCheck += (_, _) => _dirty = true;
        _manual.CheckedChanged += (_, _) => { InvalidatePeer(); Refresh(); };
        _ip.TextChanged += (_, _) => InvalidatePeer(); _port.ValueChanged += (_, _) => InvalidatePeer();
        _peer.SelectedIndexChanged += (_, _) => InvalidatePeer();
        _network.SelectedIndexChanged += async (_, _) => { InvalidatePeer(); await LoadPeersAsync(); };
        _verify.Click += async (_, _) => await RunAsync(async token => { await VerifyAsync(token); _status.Text = "Đã xác minh máy nguồn: " + _verifiedName; });
        _pick.Click += async (_, _) =>
        {
            using var picker = new OpenFileDialog { Title = "Chọn bộ cài LanChat", Filter = "Bộ cài EXE (*.exe)|*.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            await RunAsync(async token =>
            {
                var report = new Progress<FileProgress>(p => { if (!_closed && !token.IsCancellationRequested) { _progress.Value = p.Total > 0 ? (int)Math.Max(0, Math.Min(1000, p.Completed * 1000d / p.Total)) : 0; _status.Text = "Đang kiểm tra và sao chép bộ cài…"; } });
                var package = await Task.Run(() => updates.Service.Packages.ImportAsync(picker.FileName, report, token), token);
                token.ThrowIfCancellationRequested(); _package = package; _dirty = true; _status.Text = "Bộ cài đã sẵn sàng. Bấm Lưu để áp dụng.";
            });
        };
        _save.Click += async (_, _) =>
        {
            var saved = false;
            await RunAsync(async token =>
        {
            if (updates.State.Busy) throw new InvalidOperationException("Hãy hoàn tất hoặc hủy tác vụ cập nhật trước.");
            if (_source.SelectedIndex == (int)UpdateSourceKind.Lan) await VerifyAsync(token);
            var network = _network.SelectedItem as UpdateNetworkChoice;
            var candidate = new UpdateSettings((UpdateSourceKind)_source.SelectedIndex, network?.Id ?? Guid.Empty,
                _verified?.NodeId ?? Guid.Empty, _verified?.Address ?? "", _verified?.Port ?? 54546, _verifiedName, _package, _share.Checked,
                _allowed.CheckedItems.Cast<UpdateNetworkChoice>().Select(n => n.Id).ToArray());
            await updates.ConfigureAsync(candidate, token);
            _dirty = false; saved = true;
        });
            if (saved && !_closed) DialogResult = DialogResult.OK;
        };
        async Task RunAsync(Func<CancellationToken, Task> action)
        {
            if (_operation is not null || _closed) return;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _operation = operation; Refresh();
            try { await action(operation.Token); }
            catch (OperationCanceledException) { if (!_closed) _status.Text = "Đã hủy. Cài đặt trước đó được giữ nguyên."; }
            catch (Exception ex) { if (!_closed) _status.Text = ex.Message; }
            finally { _operation = null; Refresh(); }
        }
        void OnUpdateChanged() => Refresh(); updates.Changed += OnUpdateChanged;
        FormClosing += (_, e) =>
        {
            if (_operation is not null) { e.Cancel = true; _operation.Cancel(); _status.Text = "Đang hoàn tất hoặc hủy tác vụ. Vui lòng chờ rồi đóng lại."; }
            else if (DialogResult != DialogResult.OK && _dirty && !UiInteraction.Discard(this, "Bỏ các thay đổi nguồn cập nhật chưa lưu?")) e.Cancel = true;
        };
        Disposed += (_, _) => { if (_closed) return; _closed = true; updates.Changed -= OnUpdateChanged; _lifetime.Cancel(); _lifetime.Dispose(); };
        Shown += async (_, _) => { _source.Focus(); await LoadPeersAsync(); Refresh(); };
        Refresh();
    }
    private async Task LoadPeersAsync()
    {
        var generation = ++_peerGeneration; var network = _network.SelectedItem as UpdateNetworkChoice;
        _populatingPeers = true;
        try { _peer.Items.Clear(); }
        finally { _populatingPeers = false; }
        if (network?.Services?.GetService(typeof(IChatStore)) is not IChatStore store) return;
        try
        {
            var people = await Task.Run(() => store.GetUsersAsync(ct: _lifetime.Token));
            if (_closed || generation != _peerGeneration) return;
            _populatingPeers = true;
            try
            {
                _peer.Items.AddRange(people.Where(p => p.NodeId != Guid.Empty).Select(p => new PeerChoice(p)).ToArray());
                var saved = _updates.Service.Settings.NodeId;
                var index = _peer.Items.Cast<PeerChoice>().ToList().FindIndex(p => p.User.NodeId == saved);
                _peer.SelectedIndex = index >= 0 ? index : _peer.Items.Count > 0 ? 0 : -1;
                if (index < 0 && _updates.Service.Settings.Kind == UpdateSourceKind.Lan && _updates.Service.Settings.NetworkId == network.Id)
                    _manual.Checked = true;
            }
            finally { _populatingPeers = false; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && generation == _peerGeneration) _status.Text = ex.Message; }
    }
    private async Task VerifyAsync(CancellationToken ct)
    {
        if (_network.SelectedItem is not UpdateNetworkChoice network || network.Services is null) throw new IOException("Hãy chọn một mạng chat đang kết nối.");
        var person = (_peer.SelectedItem as PeerChoice)?.User;
        var address = _manual.Checked ? _ip.Text.Trim() : person?.IPAddress;
        var port = _manual.Checked ? (int)_port.Value : person?.ServicePort ?? 0;
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast) || ip.GetAddressBytes()[0] >= 224 || port < 1)
            throw new ArgumentException("Hãy chọn máy hoặc nhập địa chỉ IPv4 và cổng hợp lệ.");
        var transport = (IPeerTransport)network.Services.GetService(typeof(IPeerTransport))!;
        var store = (IChatStore)network.Services.GetService(typeof(IChatStore))!;
        NodeInfoDto info;
        try { info = await Task.Run(() => transport.GetNodeInfoAsync(address!, port, ct), ct); }
        catch (System.Security.Authentication.AuthenticationException ex)
        { throw new IOException("Không xác thực được máy nguồn. Hãy chọn máy thuộc cùng mạng chat.", ex); }
        var expected = _manual.Checked ? (_updates.Service.Settings.NetworkId == network.Id && _updates.Service.Settings.Address == address ? _updates.Service.Settings.NodeId : Guid.Empty) : person?.NodeId ?? Guid.Empty;
        if (expected != Guid.Empty && info.NodeId != expected) throw new IOException("Địa chỉ này thuộc máy khác. Hãy chọn lại máy nguồn.");
        await store.RememberPeerAsync(info, address!, ct);
        var peer = new PeerEndpoint(info.NodeId, address!, info.ServicePort);
        var client = (ILanUpdateClient)network.Services.GetService(typeof(ILanUpdateClient))!;
        await Task.Run(() => client.CheckAsync(peer, ct), ct);
        ct.ThrowIfCancellationRequested();
        _verified = peer; _verifiedName = new PersonName(info.User.DisplayName, info.User.Department) + " · " + info.DeviceName;
    }
    private sealed record PeerChoice(UserSummary User)
    { public override string ToString() => new PersonName(User.DisplayName, User.Department) + " · " + User.DeviceName; }
}
