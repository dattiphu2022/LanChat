using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class SetupForm : Form
{
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, MaxLength = 100, Text = Environment.UserName };
    private readonly TextBox _code = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    public string DisplayName => _name.Text.Trim();
    public string Invitation { get; private set; } = "";
    public SetupForm()
    {
        Text = "Bắt đầu với LanChat"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(640, 420);
        MinimumSize = new Size(590, 440); Font = new Font("Segoe UI", 10); BackColor = Color.White;
        var layout = Theme.Stack(7); layout.Padding = new Padding(24);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
        layout.Controls.Add(Theme.Label("Trò chuyện trong mạng nội bộ", 20, true), 0, 0);
        layout.Controls.Add(Theme.Label("Máy đầu tiên tạo mạng chat. Các máy còn lại dùng cùng mã tham gia.\nMỗi máy giữ dữ liệu riêng; không cần máy chủ cố định."), 0, 1);
        layout.Controls.Add(Theme.Label("Tên hiển thị của bạn"), 0, 2); layout.Controls.Add(_name, 0, 3);
        layout.Controls.Add(Theme.Label("Mã tham gia (để trống nếu bạn tạo mạng chat mới)"), 0, 4); layout.Controls.Add(_code, 0, 5);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var join = Theme.Button("Tham gia"); var create = Theme.Button("Tạo mạng chat");
        actions.Controls.Add(join); actions.Controls.Add(create); layout.Controls.Add(actions, 0, 6); Controls.Add(layout);
        create.Click += (_, _) => Finish(true); join.Click += (_, _) => Finish(false);
    }
    private void Finish(bool create)
    {
        try
        {
            if (DisplayName.Length == 0) throw new ArgumentException("Vui lòng nhập tên hiển thị.");
            Invitation = create ? ClusterConfigurationStore.CreateInvitation() : _code.Text.Trim();
            ClusterConfigurationStore.ParseInvitation(Invitation); DialogResult = DialogResult.OK;
        }
        catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "Kiểm tra thông tin"); }
    }
}
