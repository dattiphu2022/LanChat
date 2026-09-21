using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class ClusterDiagnosticsForm : Form
{
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
    private readonly Label _summary = Theme.Label("");
    public ClusterDiagnosticsForm(IChatStore store, NodeOptions options, NetworkHealth health)
    {
        Text = "Chẩn đoán mạng chat"; ClientSize = new Size(1000, 520); StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
        var layout = Theme.Stack(3); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.Controls.Add(_summary, 0, 0); layout.Controls.Add(_grid, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill }; var refresh = Theme.Button("Làm mới"); var copy = Theme.Button("Sao chép chẩn đoán");
        buttons.Controls.Add(refresh); buttons.Controls.Add(copy); layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
        async Task LoadAsync()
        {
            try
            {
                var users = await store.GetUsersAsync(); var identity = await store.GetIdentityAsync();
                _summary.Text = $"Node: {identity.NodeId}\nCluster: {options.ClusterId}\nPeers trực tuyến: {users.Count(x => x.Presence == Domain.PresenceStatus.Online)}  ·  Tin chờ gửi: {await store.GetPendingCountAsync()}\n{health.Error ?? "Phase 1: kết nối trực tiếp; chưa bật Leader / replication toàn cluster."}";
                _grid.DataSource = users.Select(x => new { Node = x.NodeId, IP = x.IPAddress, Port = x.ServicePort, User = x.DisplayName, State = x.Presence,
                    LastSeen = x.LastSeenTicks > 0 ? new DateTime(x.LastSeenTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm:ss") : "" }).ToList();
            }
            catch (Exception ex) { _summary.Text = ex.Message; }
        }
        Shown += async (_, _) => await LoadAsync(); refresh.Click += async (_, _) => await LoadAsync();
        copy.Click += (_, _) =>
        {
            var rows = _grid.Rows.Cast<DataGridViewRow>().Select(r => string.Join("\t", r.Cells.Cast<DataGridViewCell>().Select(c => c.Value?.ToString())));
            Clipboard.SetText(_summary.Text + Environment.NewLine + string.Join(Environment.NewLine, rows));
        };
    }
}
