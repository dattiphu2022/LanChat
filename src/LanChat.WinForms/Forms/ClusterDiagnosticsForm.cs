using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class ClusterDiagnosticsForm : DpiForm
{
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
    };
    private readonly TextBox _summary = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    public ClusterDiagnosticsForm(IChatStore store, NodeOptions options, NetworkHealth health, IDistributedStore distributed,
        IConsensusService consensus, ReplicationBackgroundService replication, PeerDiscoveryBackgroundService discovery, FaultInjection faults)
    {
        Icon = AppIcon.Value;
        Text = "Chẩn đoán mạng chat"; ClientSize = new Size(1080, 650); StartPosition = FormStartPosition.CenterParent;
        var layout = Theme.Stack(3);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_summary, 0, 0); layout.Controls.Add(_grid, 0, 1);
        var lifetime = new CancellationTokenSource();
        var buttons = Theme.Toolbar(); var refresh = Theme.Button("Làm mới", UiIcon.Refresh); var copy = Theme.Button("Sao chép chẩn đoán", UiIcon.Copy);
        buttons.Controls.Add(refresh); buttons.Controls.Add(copy); layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
        async Task LoadAsync()
        {
            try
            {
                var users = await store.GetUsersAsync(ct: lifetime.Token); var identity = await store.GetIdentityAsync(lifetime.Token);
                var state = await consensus.GetStatusAsync(lifetime.Token); var sync = await distributed.GetDistributedDiagnosticsAsync(lifetime.Token);
                var peers = await distributed.GetPeerDiagnosticsAsync(lifetime.Token);
                var pending = await store.GetPendingCountAsync(lifetime.Token);
                static string Time(long ticks) => ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm:ss") : "—";
                if (IsDisposed) return;
                _summary.Text = $"Node: {identity.NodeId}\r\nCluster: {options.ClusterId}\r\nVai trò: {state.Role} · Term: {state.Term} · Leader: {state.LeaderNodeId}\r\nVoters: {state.Voters.Length} · Joint: {state.JointVoters?.Length ?? 0} · Commit: {state.CommitIndex} · Quorum cục bộ: {state.HasQuorum}\r\nPeers trực tuyến: {users.Count(x => x.Presence == Domain.PresenceStatus.Online)} · Events: {sync.EventCount} · Outbox: {sync.OutboxPending} · Tin chờ: {pending}\r\nHeartbeat: {Time(state.LastLeaderHeartbeatTicks)} · Đồng bộ thành công: {Time(faults.LastSync.UtcDateTime.Ticks)}\r\n{UiText.HideIpAddresses(health.Error ?? state.StatusText)}";
                _grid.DataSource = peers.Select(x => new { Node = x.NodeId,
                    User = users.FirstOrDefault(u => u.NodeId == x.NodeId) is { } user ? new PersonName(user.DisplayName, user.Department).ToString() : null,
                    x.Role, x.Term,
                    LastSeen = Time(x.LastSeenTicks), Cursor = sync.Cursors.FirstOrDefault(c => c.OriginNodeId == x.NodeId)?.LastOriginSequence ?? 0 }).ToList();
                _grid.Tag = string.Join("\n", sync.Cursors.Select(c => $"Cursor {c.OriginNodeId}: {c.LastOriginSequence}"));
            }
            catch (Exception ex) { if (!IsDisposed) _summary.Text = UiText.HideIpAddresses(ex.Message); }
        }
        Shown += async (_, _) => await LoadAsync(); refresh.Click += async (_, _) => await LoadAsync();
        copy.Click += (_, _) => Clipboard.SetText(BuildReport());
#if DEBUG
        foreach (var label in new[] { "Pause Replication", "Stop Heartbeat", "Simulate Leader Failure" })
        {
            var check = new CheckBox { Text = label, AutoSize = true, Margin = new Padding(10) };
            check.Checked = label == "Pause Replication" ? faults.PauseReplication : label == "Stop Heartbeat" ? faults.StopHeartbeat : faults.SimulateLeaderFailure;
            check.CheckedChanged += (_, _) => { if (label == "Pause Replication") faults.PauseReplication = check.Checked; else if (label == "Stop Heartbeat") faults.StopHeartbeat = check.Checked; else faults.SimulateLeaderFailure = check.Checked; };
            buttons.Controls.Add(check);
        }
        var discover = Theme.Button("Force Discovery", UiIcon.Scan); discover.Click += (_, _) => discovery.RequestDiscovery(); buttons.Controls.Add(discover);
        var forceSync = Theme.Button("Force Sync", UiIcon.Sync); buttons.Controls.Add(forceSync);
        forceSync.Click += async (_, _) => { forceSync.Enabled = false; try { await replication.SynchronizeAsync(lifetime.Token); await LoadAsync(); } catch (Exception ex) { if (!IsDisposed) _summary.Text = UiText.HideIpAddresses(ex.Message); } finally { if (!IsDisposed) forceSync.Enabled = true; } };
#endif
        FormClosed += (_, _) => lifetime.Cancel();
    }

    internal string BuildReport()
    {
        var rows = _grid.Rows.Cast<DataGridViewRow>().Select(r =>
            string.Join("\t", r.Cells.Cast<DataGridViewCell>().Select(c => c.Value?.ToString())));
        return _summary.Text + Environment.NewLine + _grid.Tag + Environment.NewLine + string.Join(Environment.NewLine, rows);
    }
}
