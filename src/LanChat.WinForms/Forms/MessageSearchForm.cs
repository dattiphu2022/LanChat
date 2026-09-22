using LanChat.Application;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class MessageSearchForm : Form
{
    public Guid? SelectedConversation { get; private set; }
    public Guid? SelectedMessage { get; private set; }
    public MessageSearchForm(IDistributedStore store, IReadOnlyList<ConversationSummary> conversations)
    {
        Text = "Tìm trong lịch sử tin nhắn"; ClientSize = new Size(760, 500); StartPosition = FormStartPosition.CenterParent;
        var layout = Theme.Stack(3); layout.Padding = new Padding(12); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var search = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Nhập từ cần tìm (có thể gõ không dấu)…" };
        var results = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true }; var button = Theme.Button("Tìm kiếm");
        button.Click += async (_, _) =>
        {
            button.Enabled = false;
            try { var rows = await store.SearchMessagesAsync(search.Text); results.Items.Clear(); results.Items.AddRange(rows.Select(x => new ResultItem(x, $"{conversations.FirstOrDefault(c => c.Id == x.ConversationId)?.DisplayName} · {x.Text.ReplaceLineEndings(" ")}")).ToArray()); if (rows.Length == 0) results.Items.Add("Không tìm thấy tin nhắn."); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message); }
            finally { if (!IsDisposed) button.Enabled = true; }
        };
        results.DoubleClick += (_, _) => { if (results.SelectedItem is ResultItem item) { SelectedConversation = item.Result.ConversationId; SelectedMessage = item.Result.MessageId; DialogResult = DialogResult.OK; Close(); } };
        layout.Controls.Add(search, 0, 0); layout.Controls.Add(results, 0, 1); layout.Controls.Add(button, 0, 2); Controls.Add(layout); AcceptButton = button;
    }
    private sealed record ResultItem(SearchResult Result, string Label) { public override string ToString() => Label; }
}
