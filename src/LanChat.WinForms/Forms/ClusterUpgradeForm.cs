using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class ClusterUpgradeForm : DpiForm
{
    public ClusterUpgradeForm(NodeOptions options)
    {
        Icon = AppIcon.Value;
        Text = "Nâng cấp mạng chat"; ClientSize = new Size(680, 460); MinimumSize = new Size(480, 340); StartPosition = FormStartPosition.CenterParent;
        var layout = Theme.Stack(6); layout.Padding = new Padding(20);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var introduction = Theme.Label("Dữ liệu hiện tại sẽ được giữ nguyên.\nChọn một máy tạo mã nâng cấp cho cả mạng chat. Các máy khác nhập cùng mã đó.\nMã mới bật kết nối mã hóa và cơ chế tự chuyển máy điều phối.");
        UiInteraction.Wrap(introduction, layout); layout.Controls.Add(introduction, 0, 0);
        var code = new TextBox { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "Mã nâng cấp" }; layout.Controls.Add(code, 0, 1);
        var create = Theme.Button("Tạo mã nâng cấp trên máy này", UiIcon.Key); layout.Controls.Add(create, 0, 2);
        var apply = Theme.Button("Áp dụng mã nâng cấp", UiIcon.Check); layout.Controls.Add(apply, 0, 3);
        var status = Theme.Label(""); UiInteraction.Wrap(status, layout); layout.Controls.Add(status, 0, 4);
        var footer = Theme.Toolbar(); footer.FlowDirection = FlowDirection.RightToLeft; footer.Controls.Add(UiInteraction.CloseButton(this, "Hủy"));
        layout.Controls.Add(footer, 0, 5); Controls.Add(layout);
        var busy = false;
        void UpdateButtons() { create.Enabled = code.Enabled = !busy; apply.Enabled = !busy && !string.IsNullOrWhiteSpace(code.Text); }
        code.TextChanged += (_, _) => UpdateButtons();
        async Task RunAsync(Func<Task> action)
        {
            if (busy) return;
            busy = true; UpdateButtons(); status.Text = "Đang xử lý mã nâng cấp…";
            try { await action(); }
            catch (Exception ex) { status.Text = UiText.HideIpAddresses(ex.Message); }
            finally { busy = false; if (!IsDisposed) UpdateButtons(); }
        }
        create.Click += async (_, _) => await RunAsync(async () =>
        {
            var node = await ClusterConfigurationStore.ReadExistingNodeIdAsync(options.DataDirectory);
            code.Text = ClusterConfigurationStore.CreateUpgradeInvitation(options, node);
            status.Text = "Đã tạo mã. Sao chép mã cho các máy còn lại, sau đó bấm Áp dụng mã nâng cấp.";
            code.Focus(); code.SelectAll();
        });
        apply.Click += async (_, _) => await RunAsync(async () =>
        {
            var value = code.Text.Trim();
            var invite = ClusterConfigurationStore.ParseInvitation(value);
            if (invite.ClusterId != options.ClusterId || !FrameworkIO.FixedEquals(Convert.FromBase64String(invite.Secret), options.ClusterSecret))
                throw new ArgumentException("Mã không thuộc mạng chat hiện tại. Không thể ghép database của hai mạng khác nhau.");
            await ClusterConfigurationStore.SaveAsync(options, value);
            busy = false; DialogResult = DialogResult.OK;
        });
        FormClosing += (_, e) =>
        {
            if (busy) { e.Cancel = true; status.Text = "Đang lưu cấu hình mạng. Vui lòng chờ hoàn tất."; }
            else if (DialogResult != DialogResult.OK && code.Text.Length > 0 && !UiInteraction.Discard(this, "Đóng mà chưa áp dụng mã nâng cấp?")) e.Cancel = true;
        };
        Shown += (_, _) => code.Focus();
        UpdateButtons();
    }
}
