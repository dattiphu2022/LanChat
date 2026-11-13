using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class SettingsForm : Form
{
    private readonly TextBox _name = new() { Dock = DockStyle.Top, MaxLength = 100, AccessibleName = "Tên" };
    private readonly TextBox _department = new() { Dock = DockStyle.Top, MaxLength = 200, AccessibleName = "Đơn vị" };
    private readonly CheckBox _startup = new() { Dock = DockStyle.Fill, Text = "Khởi động cùng hệ thống" };

    public SettingsForm(string name, string? department, bool startup, Func<string, string, bool, Task> save)
    {
        Text = "Cài đặt LanChat";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(400, 242);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        _name.Text = name;
        _department.Text = department ?? "";
        _startup.Checked = startup;

        var layout = Theme.Stack(7);
        layout.Padding = new Padding(18, 12, 18, 10);
        foreach (var height in new[] { 23, 35, 23, 35, 41, 22 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var nameLabel = Theme.Label("&Tên");
        var departmentLabel = Theme.Label("Đơn &vị");
        nameLabel.TabIndex = 0; _name.TabIndex = 1;
        departmentLabel.TabIndex = 2; _department.TabIndex = 3; _startup.TabIndex = 4;
        layout.Controls.Add(nameLabel, 0, 0); layout.Controls.Add(_name, 0, 1);
        layout.Controls.Add(departmentLabel, 0, 2); layout.Controls.Add(_department, 0, 3);
        layout.Controls.Add(_startup, 0, 4);
        var feedback = Theme.Label(""); feedback.ForeColor = Color.Firebrick;
        layout.Controls.Add(feedback, 0, 5);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var apply = Theme.Button("Lưu");
        var cancel = Theme.Secondary("Hủy"); cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(apply); buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 6); Controls.Add(layout);
        AcceptButton = apply; CancelButton = cancel;
        var saving = false;
        FormClosing += (_, e) => { if (saving) e.Cancel = true; };
        apply.Click += async (_, _) =>
        {
            var value = _name.Text.Trim();
            if (value.Length == 0) { feedback.Text = "Vui lòng nhập tên."; _name.Focus(); return; }
            saving = true; layout.Enabled = false; feedback.Text = "";
            try
            {
                await save(value, _department.Text.Trim(), _startup.Checked);
                saving = false;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { feedback.Text = ex.Message; }
            finally { saving = false; if (!IsDisposed) layout.Enabled = true; }
        };
    }
}
