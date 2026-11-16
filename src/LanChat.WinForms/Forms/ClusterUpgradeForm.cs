using System.Security.Cryptography;
using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class ClusterUpgradeForm : DpiForm
{
    public ClusterUpgradeForm(NodeOptions options)
    {
        Icon = AppIcon.Value;
        Text = "Nâng cấp mạng chat"; ClientSize = new Size(680, 400); StartPosition = FormStartPosition.CenterParent;
        var layout = Theme.Stack(4); layout.Padding = new Padding(20);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Theme.Label("Dữ liệu hiện tại sẽ được giữ nguyên.\nChọn một máy tạo mã nâng cấp cho cả mạng chat. Các máy khác nhập cùng mã đó.\nMã mới bật kết nối mã hóa và cơ chế tự chuyển máy điều phối."), 0, 0);
        var code = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical }; layout.Controls.Add(code, 0, 1);
        var create = Theme.Button("Tạo mã nâng cấp trên máy này"); layout.Controls.Add(create, 0, 2);
        var apply = Theme.Button("Áp dụng mã nâng cấp"); layout.Controls.Add(apply, 0, 3); Controls.Add(layout);
        create.Click += async (_, _) =>
        {
            try
            {
                var node = await ClusterConfigurationStore.ReadExistingNodeIdAsync(options.DataDirectory);
                code.Text = ClusterConfigurationStore.CreateUpgradeInvitation(options, node);
            }
            catch (Exception ex) { MessageBox.Show(this, UiText.HideIpAddresses(ex.Message), "Không tạo được mã nâng cấp"); }
        };
        apply.Click += async (_, _) =>
        {
            try
            {
                var invite = ClusterConfigurationStore.ParseInvitation(code.Text);
                if (invite.ClusterId != options.ClusterId || !FrameworkIO.FixedEquals(Convert.FromBase64String(invite.Secret), options.ClusterSecret))
                    throw new ArgumentException("Mã không thuộc mạng chat hiện tại. Không thể ghép database của hai mạng khác nhau.");
                await ClusterConfigurationStore.SaveAsync(options, code.Text); DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(this, UiText.HideIpAddresses(ex.Message), "Không nâng cấp được"); }
        };
    }
}
