using LanChat.Application;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed class ForwardRecipientsForm : DpiForm
{
    private sealed record Choice(ForwardDestination Destination, string Detail)
    { public override string ToString() => Destination.Name + (Detail.Length > 0 ? " · " + Detail : ""); }
    private readonly Choice[] _choices;
    private readonly HashSet<(Guid Id, bool Group)> _selected = [];
    private readonly PromptTextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Tìm tên, đơn vị hoặc máy…" };
    private readonly Label _count = Theme.Label("");
    private readonly Button _send = Theme.Button("Chuyển tiếp", UiIcon.Forward);
    private readonly CheckedListBox _people = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true };
    private readonly CheckedListBox _groups = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true };
    private bool _binding;
    internal ForwardDestination[] Selected => _choices.Where(c => _selected.Contains((c.Destination.Id, c.Destination.Group))).Select(c => c.Destination).ToArray();
    internal ForwardRecipientsForm(IEnumerable<UserSummary> contacts, IEnumerable<ConversationSummary> conversations)
    {
        Text = "Chuyển tiếp đến nhiều người"; Icon = AppIcon.Value;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(570, 510); MinimumSize = new Size(380, 350);
        _choices = contacts.Select(u => new Choice(new(u.UserId, false, u.DisplayName),
            string.Join(" · ", new[] { u.Department, u.DeviceName, u.Presence == LanChat.Domain.PresenceStatus.Online ? "Trực tuyến" : "Ngoại tuyến" }.Where(v => !string.IsNullOrWhiteSpace(v)))))
            .Concat(conversations.Where(c => c.Kind == LanChat.Domain.ConversationKind.Group).Select(c => new Choice(new(c.Id, true, c.DisplayName), "Nhóm")))
            .GroupBy(c => (c.Destination.Id, c.Destination.Group)).Select(g => g.First()).OrderBy(c => c.Destination.Name).ToArray();
        var layout = Theme.Stack(4); layout.Padding = new Padding(12);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var people = new TabPage("Danh bạ"); people.Controls.Add(_people);
        var groups = new TabPage("Nhóm"); groups.Controls.Add(_groups); tabs.TabPages.AddRange([people, groups]);
        var buttons = Theme.Toolbar(); var cancel = Theme.Secondary("Hủy", UiIcon.Close);
        cancel.DialogResult = DialogResult.Cancel; CancelButton = cancel;
        _send.DialogResult = DialogResult.OK; AcceptButton = _send;
        buttons.Controls.AddRange([_send, cancel]);
        layout.Controls.Add(_search, 0, 0); layout.Controls.Add(tabs, 0, 1); layout.Controls.Add(_count, 0, 2); layout.Controls.Add(buttons, 0, 3); Controls.Add(layout);
        _search.TextChanged += (_, _) => Bind();
        foreach (var list in new[] { _people, _groups }) list.ItemCheck += (_, e) =>
        {
            if (_binding || list.Items[e.Index] is not Choice item) return;
            var key = (item.Destination.Id, item.Destination.Group);
            if (e.NewValue == CheckState.Checked) _selected.Add(key); else _selected.Remove(key);
            UpdateCount();
        };
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Down) { (tabs.SelectedIndex == 0 ? _people : _groups).Focus(); e.SuppressKeyPress = true; } };
        Shown += (_, _) => _search.Focus(); Bind();
    }
    private void UpdateCount()
    {
        _count.Text = $"Đã chọn {_selected.Count} người/nhóm · Danh bạ gồm cả người chưa từng nhắn và đang offline.";
        _count.MaximumSize = new Size(Math.Max(1, ClientSize.Width - 30), 0);
        _send.Enabled = _selected.Count > 0;
    }
    private void Bind()
    {
        _binding = true;
        try
        {
            foreach (var list in new[] { _people, _groups })
            {
                list.BeginUpdate(); list.Items.Clear();
                foreach (var item in _choices.Where(c => c.Destination.Group == (list == _groups) && MentionDraft.Matches(c.ToString(), _search.Text.Trim())))
                    list.Items.Add(item, _selected.Contains((item.Destination.Id, item.Destination.Group)));
                list.EndUpdate();
            }
        }
        finally { _binding = false; UpdateCount(); }
    }
}
