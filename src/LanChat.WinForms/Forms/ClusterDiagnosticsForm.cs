using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class ClusterDiagnosticsForm : Form
{
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
    private readonly Label _summary = Theme.Label("");
    public ClusterDiagnosticsForm(IChatStore store, NodeOptions options, NetworkHealth health, IDistributedStore distributed,
        IConsensusService consensus, ReplicationBackgroundService replication, PeerDiscoveryBackgroundService discovery, FaultInjection faults)
    {
        Text = "Chẩn đoán mạng chat"; ClientSize = new Size(1080, 650); StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
        var layout = Theme.Stack(3); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 180)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        layout.Controls.Add(_summary, 0, 0); layout.Controls.Add(_grid, 0, 1);
        var lifetime = new CancellationTokenSource();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true }; var refresh = Theme.Button("Làm mới"); var copy = Theme.Button("Sao chép chẩn đoán");
        buttons.Controls.Add(refresh); buttons.Controls.Add(copy); layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
        async Task LoadAsync()
        {
            try
            {
                var users = await store.GetUsersAsync(ct: lifetime.Token); var identity = await store.GetIdentityAsync(lifetime.Token);
                var state = await consensus.GetStatusAsync(lifetime.Token); var sync = await distributed.GetDistributedDiagnosticsAsync(lifetime.Token);
                var peers = await distributed.GetPeerDiagnosticsAsync(lifetime.Token);
                static string Time(long ticks) => ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm:ss") : "—";
                if (IsDisposed) return;
                _summary.Text = $"Node: {identity.NodeId}\nCluster: {options.ClusterId}\nVai trò: {state.Role} · Term: {state.Term} · Leader: {state.LeaderNodeId}\nVoters: {state.Voters.Length} · Joint: {state.JointVoters?.Length ?? 0} · Commit: {state.CommitIndex} · Quorum cục bộ: {state.HasQuorum}\nPeers trực tuyến: {users.Count(x => x.Presence == Domain.PresenceStatus.Online)} · Events: {sync.EventCount} · Outbox: {sync.OutboxPending} · Tin chờ: {await store.GetPendingCountAsync(lifetime.Token)}\nHeartbeat: {Time(state.LastLeaderHeartbeatTicks)} · Đồng bộ thành công: {Time(faults.LastSync.UtcDateTime.Ticks)}\n{health.Error ?? state.StatusText}";
                _grid.DataSource = peers.Select(x => new { Node = x.NodeId, IP = x.Address, User = users.FirstOrDefault(u => u.NodeId == x.NodeId)?.DisplayName, x.Role, x.Term,
                    LastSeen = Time(x.LastSeenTicks), Cursor = sync.Cursors.FirstOrDefault(c => c.OriginNodeId == x.NodeId)?.LastOriginSequence ?? 0 }).ToList();
                _grid.Tag = string.Join("\n", sync.Cursors.Select(c => $"Cursor {c.OriginNodeId}: {c.LastOriginSequence}"));
            }
            catch (Exception ex) { if (!IsDisposed) _summary.Text = ex.Message; }
        }
        Shown += async (_, _) => await LoadAsync(); refresh.Click += async (_, _) => await LoadAsync();
        copy.Click += (_, _) =>
        {
            var rows = _grid.Rows.Cast<DataGridViewRow>().Select(r => string.Join("\t", r.Cells.Cast<DataGridViewCell>().Select(c => c.Value?.ToString())));
            Clipboard.SetText(_summary.Text + Environment.NewLine + _grid.Tag + Environment.NewLine + string.Join(Environment.NewLine, rows));
        };
#if DEBUG
        foreach (var label in new[] { "Pause Replication", "Stop Heartbeat", "Simulate Leader Failure" })
        {
            var check = new CheckBox { Text = label, AutoSize = true, Margin = new Padding(10) };
            check.Checked = label == "Pause Replication" ? faults.PauseReplication : label == "Stop Heartbeat" ? faults.StopHeartbeat : faults.SimulateLeaderFailure;
            check.CheckedChanged += (_, _) => { if (label == "Pause Replication") faults.PauseReplication = check.Checked; else if (label == "Stop Heartbeat") faults.StopHeartbeat = check.Checked; else faults.SimulateLeaderFailure = check.Checked; };
            buttons.Controls.Add(check);
        }
        var discover = Theme.Button("Force Discovery"); discover.Click += (_, _) => discovery.RequestDiscovery(); buttons.Controls.Add(discover);
        var forceSync = Theme.Button("Force Sync"); buttons.Controls.Add(forceSync);
        forceSync.Click += async (_, _) => { forceSync.Enabled = false; try { await replication.SynchronizeAsync(lifetime.Token); await LoadAsync(); } catch (Exception ex) { if (!IsDisposed) _summary.Text = ex.Message; } finally { if (!IsDisposed) forceSync.Enabled = true; } };
#endif
        FormClosed += (_, _) => lifetime.Cancel();
    }
}
