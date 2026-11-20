using LanChat.Domain;
using LanChat.Application;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

public sealed class MessageSearchForm : DpiForm
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _searchCancellation;
    private long _request;
    private bool _stopped;
    private ListBox? _results;
    private Action? _openSelected;
    public Guid? SelectedConversation { get; private set; }
    public Guid? SelectedMessage { get; private set; }

    public MessageSearchForm(IDistributedStore store, IReadOnlyList<ConversationSummary> conversations)
        : this((query, ct) => store.SearchMessagesAsync(query, ct: ct), conversations) { }

    internal MessageSearchForm(Func<string, CancellationToken, Task<SearchResult[]>> find, IReadOnlyList<ConversationSummary> conversations)
    {
        Icon = AppIcon.Value;
        Text = "Tìm nội dung · Toàn bộ lịch sử được phép truy cập"; ClientSize = new Size(760, 500); MinimumSize = new Size(440, 320);
        StartPosition = FormStartPosition.CenterParent;
        var layout = Theme.Stack(4); layout.Padding = new Padding(12);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var search = new PromptTextBox { Dock = DockStyle.Fill, AccessibleName = "Từ cần tìm", PlaceholderText = "Nhập từ cần tìm (có thể gõ không dấu)…" };
        var results = new SearchResultListBox { Dock = DockStyle.Fill, AccessibleName = "Kết quả trong toàn bộ lịch sử được phép truy cập",
            Describe = item => item is ResultItem row ? ($"{row.Label} · {new DateTime(row.Result.CreatedAtTicks, DateTimeKind.Utc).ToLocalTime():dd/MM HH:mm}", row.Result.Text) : ("", item.ToString() ?? "") };
        var status = Theme.Label("Nhập từ cần tìm rồi nhấn Enter."); UiInteraction.Wrap(status, layout);
        var button = Theme.Button("Tìm kiếm", UiIcon.Search); button.Enabled = false;
        var cancelSearch = Theme.Secondary("Dừng tìm", UiIcon.Stop); cancelSearch.Enabled = false;
        cancelSearch.Click += (_, _) => { _request++; _searchCancellation?.Cancel(); cancelSearch.Enabled = false; button.Enabled = search.Text.Trim().Length > 0; status.Text = "Đã dừng tìm kiếm."; };
        var open = Theme.Secondary("Mở tin nhắn", UiIcon.Chat); open.Enabled = false;
        void OpenSelected()
        {
            if (results.SelectedItem is not ResultItem item) return;
            SelectedConversation = item.Result.ConversationId; SelectedMessage = item.Result.MessageId;
            DialogResult = DialogResult.OK;
        }
        _results = results; _openSelected = OpenSelected;
        results.SelectedIndexChanged += (_, _) => open.Enabled = results.SelectedItem is ResultItem;
        results.DoubleClick += (_, _) => OpenSelected();
        results.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenSelected(); } };
        open.Click += (_, _) => OpenSelected();
        search.TextChanged += (_, _) =>
        {
            _request++; _searchCancellation?.Cancel(); results.Items.Clear();
            button.Enabled = search.Text.Trim().Length > 0;
            status.Text = "Nhấn Enter hoặc bấm Tìm kiếm để tìm nội dung mới.";
        };
        button.Click += async (_, _) =>
        {
            var query = search.Text.Trim();
            if (!button.Enabled || query.Length == 0) return;
            _searchCancellation?.Cancel();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _searchCancellation = cancellation;
            var request = ++_request;
            button.Enabled = false; cancelSearch.Enabled = true; results.Query = query; results.Items.Clear(); status.Text = "Đang tìm trong toàn bộ lịch sử được phép truy cập…";
            try
            {
                var rows = await Task.Run(() => find(query, cancellation.Token), cancellation.Token);
                if (IsDisposed || cancellation.IsCancellationRequested || request != _request) return;
                results.Items.AddRange(rows.Select(x =>
                {
                    var conversation = conversations.FirstOrDefault(c => c.Id == x.ConversationId);
                    var name = new PersonName(conversation?.DisplayName ?? "", conversation?.Department);
                    return new ResultItem(x, name.ToString());
                }).ToArray());
                status.Text = rows.Length == 0 ? "Không tìm thấy tin nhắn." : $"Tìm thấy {rows.Length} tin nhắn. Chọn kết quả rồi nhấn Enter để mở.";
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { if (!IsDisposed && request == _request) status.Text = UiText.HideIpAddresses(ex.Message); }
            finally
            {
                if (ReferenceEquals(_searchCancellation, cancellation)) { _searchCancellation = null; if (!IsDisposed) cancelSearch.Enabled = false; }
                if (!IsDisposed && request == _request) button.Enabled = search.Text.Trim().Length > 0;
            }
        };
        var buttons = Theme.Toolbar(); buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Controls.Add(button); buttons.Controls.Add(cancelSearch); buttons.Controls.Add(open); buttons.Controls.Add(UiInteraction.CloseButton(this));
        layout.Controls.Add(search, 0, 0); layout.Controls.Add(results, 0, 1); layout.Controls.Add(status, 0, 2); layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout); AcceptButton = button;
        Shown += (_, _) => search.Focus();
        void Stop()
        {
            if (_stopped) return;
            _stopped = true; _request++; _lifetime.Cancel(); _searchCancellation?.Cancel();
        }
        FormClosed += (_, _) => Stop();
        Disposed += (_, _) => { Stop(); _lifetime.Dispose(); };
    }
    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter && _results?.ContainsFocus == true) { _openSelected?.Invoke(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private sealed record ResultItem(SearchResult Result, string Label) { public override string ToString() => $"{Label} · {new DateTime(Result.CreatedAtTicks, DateTimeKind.Utc).ToLocalTime():dd/MM HH:mm} · {Result.Text}"; }
}
