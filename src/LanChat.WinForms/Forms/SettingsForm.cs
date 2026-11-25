using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class SettingsForm : DpiForm
{
    private readonly TextBox _name = new() { Dock = DockStyle.Top, MaxLength = 100, AccessibleName = "Tên" };
    private readonly TextBox _department = new() { Dock = DockStyle.Top, MaxLength = 200, AccessibleName = "Đơn vị" };
    private readonly CheckBox _startup = new() { Dock = DockStyle.Fill, Text = "Khởi động cùng hệ thống", AutoSize = true };

    public SettingsForm(string name, string? department, bool startup, Func<string, string, bool, Task> save, Action? configureUpdates = null,
        UiPreferences? preferences = null, Func<UiPreferences, Task>? savePreferences = null)
    {
        Icon = AppIcon.Value;
        Text = "Cài đặt LanChat";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = preferences is null ? new Size(400, 270) : new Size(480, 520);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScroll = true;
        _name.Text = name;
        _department.Text = department ?? "";
        _startup.Checked = startup;

        var layout = Theme.Stack(8);
        layout.Dock = DockStyle.Top; layout.AutoSize = true; layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        layout.Padding = new Padding(18, 12, 18, 10);
        for (var row = 0; row < 5; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
        var fontSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, AccessibleName = "Cỡ chữ tin nhắn" };
        fontSize.Items.AddRange(new object[] { "10 pt", "11 pt", "12 pt", "14 pt" });
        var sizes = new[] { 10f,11f,12f,14f }; var initialPreferences = (preferences ?? new()).Validated();
        fontSize.SelectedIndex = Array.IndexOf(sizes, initialPreferences.MessageFontSize);
        var density = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, AccessibleName = "Mật độ hiển thị" };
        density.Items.AddRange(new object[] { "Gọn", "Tiêu chuẩn", "Thoải mái" }); density.SelectedIndex = (int)initialPreferences.Density;
        var privacy = new CheckBox { Text = "Ẩn nội dung tin nhắn trên thông báo", AutoSize = true, Dock = DockStyle.Top,
            Checked = initialPreferences.HideNotificationContent, AccessibleName = "Ẩn nội dung thông báo" };
        var theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, AccessibleName = "Giao diện" };
        theme.Items.AddRange(new object[] { "Sáng", "Tối", "Theo Windows" });
        theme.SelectedIndex = (int)initialPreferences.Theme;
        UiPreferences Appearance() => initialPreferences with { MessageFontSize = sizes[Math.Max(0,fontSize.SelectedIndex)],
            Density = (UiDensity)Math.Max(0,density.SelectedIndex), HideNotificationContent = privacy.Checked, Theme = (UiTheme)theme.SelectedIndex };
        var appearance = Theme.Stack(7); appearance.Dock = DockStyle.Top; appearance.AutoSize = true; appearance.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        for(var i=0;i<7;i++) appearance.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        appearance.Controls.Add(Theme.Label("Cỡ chữ tin nhắn"),0,0); appearance.Controls.Add(fontSize,0,1);
        appearance.Controls.Add(Theme.Label("Mật độ hiển thị"),0,2); appearance.Controls.Add(density,0,3); appearance.Controls.Add(privacy,0,4);
        appearance.Controls.Add(Theme.Label("Giao diện"),0,5); appearance.Controls.Add(theme,0,6);
        layout.Controls.Add(appearance,0,5); appearance.Visible = preferences is not null;
        var feedback = Theme.Label("");
        feedback.ForeColor = Color.Firebrick; UiInteraction.Wrap(feedback, layout);
        layout.Controls.Add(feedback, 0, 6);
        var buttons = Theme.Toolbar();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        var apply = Theme.Button("Lưu", UiIcon.Save);
        var cancel = Theme.Secondary("Hủy", UiIcon.Close); cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(apply); buttons.Controls.Add(cancel);
        if (configureUpdates is not null)
        {
            var updates = Theme.Secondary("Cài đặt cập nhật…", UiIcon.Settings);
            updates.Click += (_, _) => configureUpdates(); buttons.Controls.Add(updates);
        }
        layout.Controls.Add(buttons, 0, 7); Controls.Add(layout);
        AcceptButton = apply; CancelButton = cancel;
        var saving = false;
        FormClosing += (_, e) =>
        {
            if (saving) { e.Cancel = true; feedback.Text = "Đang lưu cài đặt. Vui lòng chờ hoàn tất."; }
            else if (DialogResult != DialogResult.OK && (_name.Text != name || _department.Text != (department ?? "") || _startup.Checked != startup || preferences is not null && Appearance() != initialPreferences) &&
                !UiInteraction.Discard(this, "Bỏ các thay đổi cài đặt chưa lưu?")) e.Cancel = true;
        };
        Shown += (_, _) => { _name.Focus(); _name.SelectAll(); };
        apply.Click += async (_, _) =>
        {
            if (saving) return;
            var value = _name.Text.Trim();
            if (value.Length == 0) { feedback.Text = "Vui lòng nhập tên."; _name.Focus(); return; }
            saving = true; appearance.Enabled = false; _name.Enabled = _department.Enabled = _startup.Enabled = buttons.Enabled = false;
            feedback.Text = "Đang lưu cài đặt…";
            try
            {
                await save(value, _department.Text.Trim(), _startup.Checked);
                if (preferences is not null && savePreferences is not null) await savePreferences(Appearance());
                saving = false;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { feedback.Text = UiText.HideIpAddresses(ex.Message); }
            finally { saving = false; if (!IsDisposed) { appearance.Enabled = true; _name.Enabled = _department.Enabled = _startup.Enabled = buttons.Enabled = true; } }
        };
    }
}
