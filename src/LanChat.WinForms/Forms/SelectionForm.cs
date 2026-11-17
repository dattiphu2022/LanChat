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
        var layout = Theme.Stack(3); layout.Padding = new Padding(12); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var select = Theme.Button("Chọn"); select.Click += (_, _) => { if (SelectedId.HasValue) { DialogResult = DialogResult.OK; Close(); } };
        search.TextChanged += (_, _) => { _list.Items.Clear(); _list.Items.AddRange(all.Where(x => x.ToString().IndexOf(search.Text, StringComparison.CurrentCultureIgnoreCase) >= 0).Cast<object>().ToArray()); };
        _list.Items.AddRange(all); _list.DoubleClick += (_, _) => select.PerformClick();
        layout.Controls.Add(search, 0, 0); layout.Controls.Add(_list, 0, 1); layout.Controls.Add(select, 0, 2); Controls.Add(layout); AcceptButton = select;
    }
    public static string? Prompt(IWin32Window owner, string title, string initial = "")
    {
        using var dialog = new DpiForm { Text = title, ClientSize = new Size(500, 130), StartPosition = FormStartPosition.CenterParent };
        var layout = Theme.Stack(2); layout.Padding = new Padding(12); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var text = new TextBox { Text = initial, MaxLength = 120, Dock = DockStyle.Fill }; var ok = Theme.Button("Lưu"); ok.DialogResult = DialogResult.OK;
        layout.Controls.Add(text, 0, 0); layout.Controls.Add(ok, 0, 1); dialog.Controls.Add(layout); dialog.AcceptButton = ok;
        return dialog.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(text.Text) ? text.Text.Trim() : null;
    }
}
