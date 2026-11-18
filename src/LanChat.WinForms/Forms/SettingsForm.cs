using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class SettingsForm : DpiForm
{
    private readonly TextBox _name = new() { Dock = DockStyle.Top, MaxLength = 100, AccessibleName = "Tên" };
    private readonly TextBox _department = new() { Dock = DockStyle.Top, MaxLength = 200, AccessibleName = "Đơn vị" };
    private readonly CheckBox _startup = new() { Dock = DockStyle.Fill, Text = "Khởi động cùng hệ thống", AutoSize = true };

    public SettingsForm(string name, string? department, bool startup, Func<string, string, bool, Task> save)
    {
        Icon = AppIcon.Value;
        Text = "Cài đặt LanChat";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(400, 270);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScroll = true;
        _name.Text = name;
        _department.Text = department ?? "";
        _startup.Checked = startup;

        var layout = Theme.Stack(7);
        layout.Dock = DockStyle.Top; layout.AutoSize = true; layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        layout.Padding = new Padding(18, 12, 18, 10);
        for (var row = 0; row < 5; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _name.Margin = _department.Margin = new Padding(3, 3, 3, 8);
        _startup.Margin = new Padding(3, 5, 3, 5);
        var nameLabel = Theme.Label("&Tên");
        var departmentLabel = Theme.Label("Đơn &vị");
        nameLabel.TabIndex = 0; _name.TabIndex = 1;
        departmentLabel.TabIndex = 2; _department.TabIndex = 3; _startup.TabIndex = 4;
        layout.Controls.Add(nameLabel, 0, 0); layout.Controls.Add(_name, 0, 1);
        layout.Controls.Add(departmentLabel, 0, 2); layout.Controls.Add(_department, 0, 3);
        layout.Controls.Add(_startup, 0, 4);
        var feedback = Theme.Label("");
        feedback.ForeColor = Color.Firebrick; UiInteraction.Wrap(feedback, layout);
        layout.Controls.Add(feedback, 0, 5);
        var buttons = Theme.Toolbar();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        var apply = Theme.Button("Lưu", UiIcon.Save);
        var cancel = Theme.Secondary("Hủy", UiIcon.Close); cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(apply); buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 6); Controls.Add(layout);
        AcceptButton = apply; CancelButton = cancel;
        var saving = false;
        FormClosing += (_, e) =>
        {
            if (saving) { e.Cancel = true; feedback.Text = "Đang lưu cài đặt. Vui lòng chờ hoàn tất."; }
            else if (DialogResult != DialogResult.OK && (_name.Text != name || _department.Text != (department ?? "") || _startup.Checked != startup) &&
                !UiInteraction.Discard(this, "Bỏ các thay đổi cài đặt chưa lưu?")) e.Cancel = true;
        };
        Shown += (_, _) => { _name.Focus(); _name.SelectAll(); };
        apply.Click += async (_, _) =>
        {
            if (saving) return;
            var value = _name.Text.Trim();
            if (value.Length == 0) { feedback.Text = "Vui lòng nhập tên."; _name.Focus(); return; }
            saving = true; _name.Enabled = _department.Enabled = _startup.Enabled = buttons.Enabled = false;
            feedback.Text = "Đang lưu cài đặt…";
            try
            {
                await save(value, _department.Text.Trim(), _startup.Checked);
                saving = false;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { feedback.Text = UiText.HideIpAddresses(ex.Message); }
            finally { saving = false; if (!IsDisposed) _name.Enabled = _department.Enabled = _startup.Enabled = buttons.Enabled = true; }
        };
    }
}
