using System.Diagnostics;
using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace LanChat.WinForms.Forms;

public sealed class SettingsForm : Form
{
    private readonly IChatStore _store;
    private readonly NodeOptions _options;
    private readonly ILocalNetworkInfoProvider _networks;
    private readonly Dictionary<string, TextBox> _fields = [];
    private LocalUserProfile? _profile;
    private readonly TextBox _network = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both };
    private readonly TextBox _cluster = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill };
    private string? _avatarPath;
    public SettingsForm(IChatStore store, NodeOptions options, ILocalNetworkInfoProvider networks, IConsensusService consensus, ILanScanner scanner, IServiceProvider services)
    {
        _store = store; _options = options; _networks = networks;
        Text = "Cài đặt LanChat"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(740, 640); MinimumSize = new Size(650, 600); Font = new Font("Segoe UI", 10);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var profile = new TabPage("Hồ sơ"); var network = new TabPage("Mạng"); var cluster = new TabPage("Mạng chat"); var storage = new TabPage("Lưu trữ");
        var diagnostics = new TabPage("Chẩn đoán");
        tabs.TabPages.AddRange([profile, network, cluster, storage, diagnostics]); Controls.Add(tabs);
        var showDiagnostics = Theme.Button("Mở chẩn đoán mạng chat"); diagnostics.Controls.Add(showDiagnostics);
        showDiagnostics.Click += (_, _) => { using var form = ActivatorUtilities.CreateInstance<ClusterDiagnosticsForm>(services); form.ShowDialog(this); };
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 10, Padding = new Padding(14) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var labels = new[] { ("DisplayName", "Tên hiển thị *"), ("FullName", "Họ và tên"), ("Nickname", "Biệt danh"), ("Department", "Đơn vị"),
            ("Position", "Chức vụ"), ("Phone", "Điện thoại"), ("StatusMessage", "Trạng thái") };
        for (var i = 0; i < labels.Length; i++)
        {
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); fields.Controls.Add(Theme.Label(labels[i].Item2), 0, i);
            var input = new TextBox { Dock = DockStyle.Top, MaxLength = labels[i].Item1 == "DisplayName" ? 100 : 200 }; _fields.Add(labels[i].Item1, input); fields.Controls.Add(input, 1, i);
        }
        var avatar = Theme.Button("Chọn ảnh đại diện…"); fields.Controls.Add(avatar, 1, 7);
        var save = Theme.Button("Lưu hồ sơ"); fields.Controls.Add(save, 1, 8); profile.Controls.Add(fields);
        avatar.Click += (_, _) => { using var picker = new OpenFileDialog { Filter = "Ảnh|*.jpg;*.jpeg;*.png;*.bmp" }; if (picker.ShowDialog(this) == DialogResult.OK) _avatarPath = picker.FileName; };
        save.Click += async (_, _) => await SaveAsync(save);
        var networkLayout = Theme.Stack(2); networkLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); networkLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        var scan = Theme.Button("Quét mạng"); networkLayout.Controls.Add(_network, 0, 0); networkLayout.Controls.Add(scan, 0, 1); network.Controls.Add(networkLayout);
        var scanLifetime = new CancellationTokenSource(); FormClosed += (_, _) => scanLifetime.Cancel();
        scan.Click += async (_, _) => { scan.Enabled = false; try { var peers = await scanner.ScanAsync(scanLifetime.Token); if (!IsDisposed) MessageBox.Show(this, $"Đã tìm thấy {peers.Count} thiết bị."); } catch (OperationCanceledException) { } catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message); } finally { if (!IsDisposed) scan.Enabled = true; } };
        var clusterLayout = Theme.Stack(3); clusterLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 150)); clusterLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); clusterLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        clusterLayout.Controls.Add(_cluster, 0, 0);
        var showCode = Theme.Button("Hiện mã mời tham gia"); clusterLayout.Controls.Add(showCode, 0, 1);
        var code = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true };
        clusterLayout.Controls.Add(code, 0, 2); cluster.Controls.Add(clusterLayout);
        showCode.Click += (_, _) => code.Text = ClusterConfigurationStore.GetInvitation(options);
        var storageLayout = Theme.Stack(3); storageLayout.Controls.Add(Theme.Label("Dữ liệu riêng trên máy này:\n" + options.DataDirectory + "\n\nNhật ký: Logs\\lanchat-*.log\nSao lưu cả thư mục khi ứng dụng đã đóng.\nMã mời cho phép tham gia mạng chat; chỉ chia sẻ với người được phép."));
        var openFolder = Theme.Button("Mở thư mục dữ liệu"); storageLayout.Controls.Add(openFolder); storage.Controls.Add(storageLayout);
        openFolder.Click += (_, _) => Process.Start(new ProcessStartInfo(options.DataDirectory) { UseShellExecute = true });
        Shown += async (_, _) =>
        {
            try
            {
                _profile = await store.GetProfileAsync(); _avatarPath = _profile.AvatarPath;
                _fields["DisplayName"].Text = _profile.DisplayName; _fields["FullName"].Text = _profile.FullName; _fields["Nickname"].Text = _profile.Nickname;
                _fields["Department"].Text = _profile.Department; _fields["Position"].Text = _profile.Position; _fields["Phone"].Text = _profile.Phone; _fields["StatusMessage"].Text = _profile.StatusMessage;
                var id = await store.GetIdentityAsync();
                var state = await consensus.GetStatusAsync();
                _cluster.Text = $"Cluster: {options.ClusterId}\r\nNode: {id.NodeId}\r\nUser: {id.UserId}\r\nLeader: {state.LeaderNodeId}\r\nPeers đã biết: {(await store.GetUsersAsync()).Count} · Dữ liệu được đồng bộ giữa các máy.";
                _network.Text = $"TCP/TLS: {options.ServicePort}   UDP: {options.DiscoveryPort}\r\nLeader: {state.LeaderNodeId}\r\nRole: {state.Role} · Term: {state.Term}\r\n\r\n" + string.Join("\r\n\r\n", networks.GetNetworks().Select(n =>
                    $"{n.Adapter}\r\nIPv4: {n.Address}/{n.PrefixLength}\r\nMask: {n.Mask}\r\nNetwork: {n.Network}    Broadcast: {n.Broadcast}\r\nHosts: {n.FirstHost} → {n.LastHost}\r\nGateway: {n.Gateway}"));
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Không tải được cài đặt"); }
        };
    }
    private async Task SaveAsync(Button button)
    {
        if (_profile is null) return;
        button.Enabled = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(_avatarPath) && _avatarPath != _profile.AvatarPath)
            {
                var info = new FileInfo(_avatarPath);
                if (info.Length > 5_000_000) throw new ArgumentException("Ảnh đại diện tối đa 5 MB.");
                using (var image = Image.FromFile(_avatarPath)) { if (image.Width > 8000 || image.Height > 8000) throw new ArgumentException("Kích thước ảnh quá lớn."); }
                var target = Path.Combine(_options.DataDirectory, "Avatar" + Path.GetExtension(_avatarPath).ToLowerInvariant());
                if (!string.Equals(target, _avatarPath, StringComparison.OrdinalIgnoreCase))
                {
                    await using var source = File.OpenRead(_avatarPath); await using var destination = File.Create(target); await source.CopyToAsync(destination);
                }
                _profile.AvatarPath = target;
            }
            _profile.DisplayName = _fields["DisplayName"].Text.Trim(); _profile.FullName = _fields["FullName"].Text.Trim(); _profile.Nickname = _fields["Nickname"].Text.Trim();
            _profile.Department = _fields["Department"].Text.Trim(); _profile.Position = _fields["Position"].Text.Trim(); _profile.Phone = _fields["Phone"].Text.Trim(); _profile.StatusMessage = _fields["StatusMessage"].Text.Trim();
            await _store.SaveProfileAsync(_profile); DialogResult = DialogResult.OK;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Không lưu được hồ sơ"); }
        finally { button.Enabled = true; }
    }
}
