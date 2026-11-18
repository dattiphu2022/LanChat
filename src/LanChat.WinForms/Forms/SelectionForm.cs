using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed class SelectionForm : DpiForm
{
    private readonly ListBox _list = new PersonNameListBox(item => ((SelectionItem)item).DisplayName) { Dock = DockStyle.Fill };
    public Guid? SelectedId => (_list.SelectedItem as SelectionItem)?.Id;
    public SelectionForm(string title, IEnumerable<SelectionItem> items)
    {
        Text = title; ClientSize = new Size(520, 420); MinimumSize = new Size(380, 300); StartPosition = FormStartPosition.CenterParent;
        var all = items.ToArray(); var search = new PromptTextBox { Dock = DockStyle.Fill, PlaceholderText = "Tìm…" };
        Icon = AppIcon.Value;
        var layout = Theme.Stack(4); layout.Padding = new Padding(12);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var status = Theme.Label(""); UiInteraction.Wrap(status, layout);
        var select = Theme.Button("Chọn", UiIcon.Check); select.Enabled = false;
        select.Click += (_, _) => { if (SelectedId.HasValue) { DialogResult = DialogResult.OK; Close(); } };
        _list.SelectedIndexChanged += (_, _) => select.Enabled = SelectedId.HasValue;
        void Filter()
        {
            var selected = SelectedId;
            var visible = all.Where(x => x.ToString().IndexOf(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0).ToArray();
            _list.BeginUpdate();
            try { _list.Items.Clear(); _list.Items.AddRange(visible); _list.SelectedIndex = Array.FindIndex(visible, x => x.Id == selected); }
            finally { _list.EndUpdate(); }
            select.Enabled = SelectedId.HasValue;
            status.Text = visible.Length == 0 ? (all.Length == 0 ? "Chưa có mục để chọn." : "Không tìm thấy mục phù hợp.") : "Chọn một mục rồi nhấn Enter hoặc bấm Chọn.";
        }
        search.TextChanged += (_, _) => Filter(); Filter();
        _list.DoubleClick += (_, _) => select.PerformClick();
        var buttons = Theme.Toolbar(); buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Controls.Add(select); buttons.Controls.Add(UiInteraction.CloseButton(this, "Hủy"));
        layout.Controls.Add(search, 0, 0); layout.Controls.Add(_list, 0, 1); layout.Controls.Add(status, 0, 2); layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout); AcceptButton = select;
        Shown += (_, _) => { _list.SelectedIndex = -1; search.Focus(); };
    }
    public static string? Prompt(IWin32Window owner, string title, string initial = "", int maxLength = 120, Func<string, Task>? save = null)
    {
        using var dialog = new DpiForm { Text = title, Icon = AppIcon.Value, ClientSize = new Size(500, 170), StartPosition = FormStartPosition.CenterParent };
        var layout = Theme.Stack(3); layout.Padding = new Padding(12);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var text = new TextBox { Text = initial, MaxLength = maxLength, Dock = DockStyle.Top, TabIndex = 0 };
        var feedback = Theme.Label(""); feedback.ForeColor = Color.Firebrick; UiInteraction.Wrap(feedback, layout);
        var ok = Theme.Button("Lưu", UiIcon.Save);
        var buttons = Theme.Toolbar(); buttons.FlowDirection = FlowDirection.RightToLeft;
        var saving = false;
        ok.Click += async (_, _) =>
        {
            if (saving) return;
            if (string.IsNullOrWhiteSpace(text.Text)) { feedback.Text = "Vui lòng nhập tên."; text.Focus(); return; }
            saving = true; text.Enabled = buttons.Enabled = false; feedback.Text = "Đang lưu…";
            try
            {
                if (save is not null) await save(text.Text.Trim());
                saving = false; dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { feedback.Text = UiText.HideIpAddresses(ex.Message); }
            finally { saving = false; if (!dialog.IsDisposed) text.Enabled = buttons.Enabled = true; }
        };
        buttons.Controls.Add(ok); buttons.Controls.Add(UiInteraction.CloseButton(dialog, "Hủy"));
        layout.Controls.Add(text, 0, 0); layout.Controls.Add(feedback, 0, 1); layout.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(layout); dialog.AcceptButton = ok;
        dialog.FormClosing += (_, e) =>
        {
            if (saving) { e.Cancel = true; feedback.Text = "Đang lưu. Vui lòng chờ hoàn tất."; }
            else if (dialog.DialogResult != DialogResult.OK && text.Text != initial && !UiInteraction.Discard(dialog, "Bỏ tên vừa nhập mà chưa lưu?")) e.Cancel = true;
        };
        dialog.Shown += (_, _) => { text.Focus(); text.SelectAll(); };
        var result = owner is Control control ? UiInteraction.ShowDialog(dialog, control) : dialog.ShowDialog(owner);
        return result == DialogResult.OK ? text.Text.Trim() : null;
    }
}
