using System.Diagnostics;
using LanChat.Application;
using LanChat.Domain;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace LanChat.WinForms.Forms;

public sealed class NetworkForm : DpiForm
{
    private readonly TextBox _network = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _cluster = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    public NetworkForm(IChatStore store, NodeOptions options, ILocalNetworkInfoProvider networks, IConsensusService consensus, ILanScanner scanner, IServiceProvider services)
    {
        Icon = AppIcon.Value;
        Text = "Quản lý kết nối mạng chat"; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(740, 640); MinimumSize = new Size(650, 450);
        var tabs = new IconTabControl(UiIcon.Network, UiIcon.People, UiIcon.Storage, UiIcon.Diagnostics, UiIcon.Key)
            { Dock = DockStyle.Fill, Multiline = true };
        var network = tabs.CreatePage("Kết nối", UiIcon.Network);
        var cluster = tabs.CreatePage("Điều phối và mã mời", UiIcon.People);
        var storage = tabs.CreatePage("Lưu trữ", UiIcon.Storage);
        var diagnostics = tabs.CreatePage("Chẩn đoán", UiIcon.Diagnostics);
        var encryption = tabs.CreatePage("Khóa mã hóa", UiIcon.Key);
        tabs.TabPages.AddRange([cluster, network, encryption, storage, diagnostics]);
        var root = Theme.Stack(2); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var footer = Theme.Toolbar(); footer.FlowDirection = FlowDirection.RightToLeft; footer.Controls.Add(UiInteraction.CloseButton(this));
        root.Controls.Add(tabs, 0, 0); root.Controls.Add(footer, 0, 1); Controls.Add(root);
        var showDiagnostics = Theme.Button("Mở chẩn đoán mạng chat", UiIcon.Diagnostics); diagnostics.Controls.Add(showDiagnostics);
        showDiagnostics.Click += (_, _) => { using var form = ActivatorUtilities.CreateInstance<ClusterDiagnosticsForm>(services); UiInteraction.ShowDialog(form, this); };
        var networkLayout = Theme.Stack(3); networkLayout.Padding = new Padding(8);
        networkLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        networkLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        networkLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var scan = Theme.Button("Quét mạng", UiIcon.Scan); scan.Dock = DockStyle.Left;
        var scanStatus = Theme.Label(""); UiInteraction.Wrap(scanStatus, networkLayout);
        networkLayout.Controls.Add(_network, 0, 0);
        networkLayout.Controls.Add(scanStatus, 0, 1);
        networkLayout.Controls.Add(scan, 0, 2);
        network.Controls.Add(networkLayout);
        var scanLifetime = new CancellationTokenSource(); FormClosed += (_, _) => scanLifetime.Cancel();
        var keysLayout = Theme.Stack(3); keysLayout.Padding = new Padding(10);
        keysLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        keysLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        keysLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        keysLayout.Controls.Add(Theme.Label("Đối chiếu dấu vân tay với mã trên máy của người liên hệ qua một kênh tin cậy, nhất là trước khi gửi nội dung nhạy cảm lần đầu."), 0, 0);
        var fingerprints = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        var refreshKeys = Theme.Secondary("Làm mới", UiIcon.Refresh); refreshKeys.Dock = DockStyle.Left;
        keysLayout.Controls.Add(fingerprints, 0, 1); keysLayout.Controls.Add(refreshKeys, 0, 2); encryption.Controls.Add(keysLayout);
        var readingKeys = false;
        async Task ReadFingerprintsAsync()
        {
            if (readingKeys || scanLifetime.IsCancellationRequested) return;
            readingKeys = true; refreshKeys.Enabled = false; fingerprints.Text = "Đang tải khóa mã hóa…";
            try
            {
                var known = await Task.Run(() => store.GetEncryptionIdentitiesAsync(scanLifetime.Token), scanLifetime.Token);
                if (!IsDisposed) fingerprints.Text = string.Join("\r\n\r\n", known.OrderByDescending(x => x.IsLocal).Select(x =>
                    $"{new PersonName(x.DisplayName, x.Department)}{(x.IsLocal ? " (máy này)" : "")}\r\n{x.Fingerprint}"));
            }
            catch (OperationCanceledException) when (scanLifetime.IsCancellationRequested) { }
            catch (Exception ex) { if (!IsDisposed) fingerprints.Text = UiText.HideIpAddresses(ex.Message); }
            finally { readingKeys = false; if (!IsDisposed) refreshKeys.Enabled = true; }
        }
        refreshKeys.Click += async (_, _) => await ReadFingerprintsAsync();
        tabs.SelectedIndexChanged += async (_, _) => { if (tabs.SelectedTab == encryption) await ReadFingerprintsAsync(); };
        CancellationTokenSource? currentScan = null;
        scan.Click += async (_, _) =>
        {
            if (currentScan is not null) { currentScan.Cancel(); return; }
            var stop = CancellationTokenSource.CreateLinkedTokenSource(scanLifetime.Token);
            currentScan = stop; scan.Text = "Dừng quét"; scan.Icon = UiIcon.Stop; scanStatus.Text = "Đang quét mạng…";
            try
            {
                var progress = new Progress<LanScanProgress>(p =>
                {
                    if (!IsDisposed && currentScan == stop)
                        scanStatus.Text = $"Đã kiểm tra {p.Checked} địa chỉ · {p.Discovered} máy";
                });
                var peers = await scanner.ScanAsync(stop.Token, progress);
                if (!IsDisposed) scanStatus.Text = $"Đã tìm thấy {peers.Count} máy.";
            }
            catch (OperationCanceledException) { if (!IsDisposed) scanStatus.Text = "Đã dừng quét."; }
            catch (Exception ex) { if (!IsDisposed) scanStatus.Text = UiText.HideIpAddresses(ex.Message); }
            finally
            {
                currentScan = null;
                stop.Dispose();
                if (!IsDisposed) { scan.Text = "Quét mạng"; scan.Icon = UiIcon.Scan; }
            }
        };
        var clusterLayout = Theme.Stack(4); clusterLayout.Padding = new Padding(8);
        clusterLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        clusterLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        clusterLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        clusterLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        clusterLayout.Controls.Add(_cluster, 0, 0);
        var leadership = Theme.Stack(3);
        leadership.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        leadership.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        leadership.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var allow = new CheckBox { Text = "Cho phép máy này làm trung tâm", Dock = DockStyle.Fill, Enabled = false };
        var actions = Theme.Toolbar();
        var campaign = Theme.Secondary("Ứng cử", UiIcon.Check); var nominate = Theme.Secondary("Đề cử máy khác", UiIcon.SwitchUser);
        actions.Controls.Add(campaign); actions.Controls.Add(nominate);
        var feedback = Theme.Label(""); feedback.ForeColor = Theme.Muted;
        UiInteraction.Wrap(feedback, leadership);
        leadership.Controls.Add(allow, 0, 0); leadership.Controls.Add(actions, 0, 1); leadership.Controls.Add(feedback, 0, 2);
        clusterLayout.Controls.Add(leadership, 0, 1);
        var showCode = Theme.Secondary("Hiện mã mời tham gia", UiIcon.Key); showCode.Dock = DockStyle.Left;
        clusterLayout.Controls.Add(showCode, 0, 2);
        var code = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        clusterLayout.Controls.Add(code, 0, 3); cluster.Controls.Add(clusterLayout);
        showCode.Click += (_, _) => code.Text = ClusterConfigurationStore.GetInvitation(options);

        var leadershipBusy = false;
        var loadingLeadership = false;
        var readingLeadership = false;
        var leadershipVersion = 0;
        var leadershipTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        async Task UpdateLeadershipAsync()
        {
            if (IsDisposed || leadershipBusy || readingLeadership || scanLifetime.IsCancellationRequested) return;
            readingLeadership = true;
            var version = leadershipVersion;
            try
            {
                var ct = scanLifetime.Token;
                var data = await Task.Run(async () => (await consensus.GetStatusAsync(ct), await store.GetIdentityAsync(ct), await store.GetUsersAsync(ct: ct)), ct);
                if (IsDisposed || leadershipBusy || scanLifetime.IsCancellationRequested || version != leadershipVersion) return;
                var (state, identity, peers) = data;
                var centerId = state.LeaderReady ? state.LeaderNodeId : state.PairCoordinatorNodeId;
                var leader = peers.FirstOrDefault(p => p.NodeId == centerId);
                var center = centerId.HasValue ? (centerId == identity.NodeId ? "Máy này" :
                    leader is null ? "Máy trong LAN" : new PersonName(leader.DisplayName, leader.Department).ToString()) : "Đang chờ";
                _cluster.Text = $"Máy trung tâm: {center}\r\n{UiText.HideIpAddresses(state.StatusText)}\r\nMáy bỏ phiếu: {state.Voters.Length}   ·   Nhiệm kỳ: {state.Term}\r\nMáy đã biết: {peers.Count}\r\nID mạng: {options.ClusterId:N} · Giao thức: v{NodeInfoDto.CurrentProtocol}";
                loadingLeadership = true; allow.Checked = state.CanLead; allow.Enabled = true; loadingLeadership = false;
                campaign.Enabled = state.CanLead && state.Role != NodeRole.Leader &&
                    state.Voters.Concat(state.JointVoters ?? []).Contains(identity.NodeId);
                nominate.Enabled = state.LeaderReady && state.Voters.Concat(state.JointVoters ?? []).Any(id => id != identity.NodeId);
            }
            finally { readingLeadership = false; }
        }
        async Task ApplyLeadershipAsync(Func<Task<LeadershipResult>> action, string success)
        {
            if (leadershipBusy) return;
            leadershipVersion++;
            leadershipBusy = true; allow.Enabled = campaign.Enabled = nominate.Enabled = false;
            feedback.Text = "Đang xử lý…";
            try
            {
                var result = await action();
                if (!IsDisposed) feedback.Text = result.Success ? success : UiText.HideIpAddresses(result.Error ?? "Đã hủy thao tác.");
            }
            catch (Exception ex) { if (!IsDisposed && !scanLifetime.IsCancellationRequested) feedback.Text = UiText.HideIpAddresses(ex.Message); }
            finally
            {
                leadershipBusy = false;
                leadershipVersion++;
                try { await UpdateLeadershipAsync(); }
                catch (Exception ex) { if (!IsDisposed && !scanLifetime.IsCancellationRequested) feedback.Text = UiText.HideIpAddresses(ex.Message); }
            }
        }
        allow.CheckedChanged += async (_, _) =>
        {
            if (!loadingLeadership && allow.Enabled)
                await ApplyLeadershipAsync(() => consensus.SetCanLeadAsync(allow.Checked, scanLifetime.Token), "Đã lưu lựa chọn.");
        };
        campaign.Click += async (_, _) =>
            await ApplyLeadershipAsync(() => consensus.CampaignAsync(scanLifetime.Token), "Đã gửi yêu cầu ứng cử.");
        nominate.Click += async (_, _) => await ApplyLeadershipAsync(async () =>
        {
            var state = await consensus.GetStatusAsync(scanLifetime.Token);
            var voters = new HashSet<Guid>(state.Voters.Concat(state.JointVoters ?? []));
            var users = await store.GetUsersAsync(ct: scanLifetime.Token);
            var candidates = users.Where(u => u.Presence == PresenceStatus.Online && voters.Contains(u.NodeId) && u.NodeId != state.LeaderNodeId)
                .Select(u => new SelectionItem(u.NodeId, u.DisplayName, u.Department)).ToArray();
            if (candidates.Length == 0) return new(false, "Chưa có máy trực tuyến để đề cử.");
            using var select = new SelectionForm("Đề cử máy trung tâm", candidates);
            return UiInteraction.ShowDialog(select, this) == DialogResult.OK && select.SelectedId is Guid id
                ? await consensus.NominateAsync(id, scanLifetime.Token) : new(false, "Đã hủy đề cử.");
        }, "Máy được đề cử đã nhận vai trò.");
        leadershipTimer.Tick += async (_, _) =>
        {
            if (tabs.SelectedTab != cluster) return;
            try { await UpdateLeadershipAsync(); }
            catch (Exception ex) { if (!IsDisposed && !scanLifetime.IsCancellationRequested) feedback.Text = UiText.HideIpAddresses(ex.Message); }
        };
        FormClosed += (_, _) => leadershipTimer.Dispose();
        FormClosing += (_, e) =>
        {
            if (leadershipBusy) { e.Cancel = true; feedback.Text = "Đang cập nhật điều phối. Vui lòng chờ hoàn tất."; }
        };
        var disposed = false;
        Disposed += (_, _) =>
        {
            if (disposed) return;
            disposed = true; scanLifetime.Cancel(); leadershipTimer.Dispose(); scanLifetime.Dispose();
        };
        var storageLayout = Theme.Stack(3); storageLayout.Controls.Add(Theme.Label("Dữ liệu riêng trên máy này:\n" + options.DataDirectory + "\n\nNhật ký: Logs\\lanchat-*.log\nSao lưu cả thư mục khi ứng dụng đã đóng.\nMã mời cho phép tham gia mạng chat; chỉ chia sẻ với người được phép."));
        var openFolder = Theme.Button("Mở thư mục dữ liệu", UiIcon.Folder); storageLayout.Controls.Add(openFolder); storage.Controls.Add(storageLayout);
        openFolder.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(options.DataDirectory) { UseShellExecute = true }); }
            catch (Exception ex) { UiInteraction.Warn(this, ex.Message, "Chưa mở được thư mục dữ liệu"); }
        };
        Shown += async (_, _) =>
        {
            try
            {
                var ct = scanLifetime.Token;
                var details = await Task.Run(async () => (State: await consensus.GetStatusAsync(ct), Count: networks.GetNetworks().Count), ct);
                if (IsDisposed) return;
                _network.Text = $"TCP/TLS: {options.ServicePort}   UDP: {options.DiscoveryPort}\r\n" +
                    $"Cấu hình IPv4 đang hoạt động: {details.Count}\r\nMáy trung tâm: {details.State.LeaderNodeId}\r\n" +
                    $"Vai trò: {details.State.Role} · Nhiệm kỳ: {details.State.Term}";
                await UpdateLeadershipAsync();
                if (!scanLifetime.IsCancellationRequested) leadershipTimer.Start();
            }
            catch (OperationCanceledException) when (scanLifetime.IsCancellationRequested) { }
            catch (Exception ex) { if (!IsDisposed) MessageBox.Show(UiInteraction.Owner(this), UiText.HideIpAddresses(ex.Message), "Không tải được cài đặt"); }
        };
    }
}
