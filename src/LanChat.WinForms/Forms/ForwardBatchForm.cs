using LanChat.Application;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed class ForwardBatchForm : DpiForm
{
    private readonly IChatService _chat;
    private readonly ForwardRequest[] _requests;
    private readonly Dictionary<ForwardRequest, ForwardResult> _results = [];
    private readonly Label _state = Theme.Label("Đang chuẩn bị chuyển tiếp…");
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000 };
    private readonly ListView _items = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
    private readonly Button _retry = Theme.Secondary("Thử lại mục chưa gửi", UiIcon.Restore);
    private readonly Button _cancel = Theme.Secondary("Hủy lượt", UiIcon.Close);
    private readonly Button _close = Theme.Secondary("Đóng", UiIcon.Close);
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _operation;
    private bool _working;
    internal ForwardBatchForm(IChatService chat, ForwardRequest[] requests, CancellationToken lifetime)
    {
        _chat = chat; _requests = requests; _lifetime = lifetime;
        _retry.Enabled = _close.Enabled = false;
        Text = "Kết quả chuyển tiếp"; Icon = AppIcon.Value; StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false; ClientSize = new Size(720, 410); MinimumSize = new Size(420, 300);
        var layout = Theme.Stack(4); layout.Padding = new Padding(12);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        UiInteraction.Wrap(_state, layout);
        _items.Columns.Add("Người/nhóm", 180); _items.Columns.Add("Tin", 60); _items.Columns.Add("Kết quả", 420);
        var buttons = Theme.Toolbar(); buttons.Controls.AddRange([_retry, _cancel, _close]);
        layout.Controls.Add(_state, 0, 0); layout.Controls.Add(_progress, 0, 1); layout.Controls.Add(_items, 0, 2); layout.Controls.Add(buttons, 0, 3); Controls.Add(layout);
        _close.Click += (_, _) => Close(); CancelButton = _close;
        _cancel.Click += (_, _) => Cancel();
        _retry.Click += async (_, _) => await RunAsync(_requests.Where(r => !_results.TryGetValue(r, out var result) || result.Outcome != ForwardOutcome.Queued).ToArray());
        FormClosing += (_, e) => { if (_working) { e.Cancel = true; Cancel(); } };
        Shown += async (_, _) => await RunAsync(_requests);
    }
    private void Cancel()
    {
        _operation?.Cancel(); _cancel.Enabled = false;
        _state.Text = "Đang dừng; giữ các tin đã lưu chờ gửi. Vui lòng chờ tác vụ kết thúc.";
    }
    private async Task RunAsync(ForwardRequest[] requests)
    {
        if (_working || requests.Length == 0 || IsDisposed) return;
        _state.Text = "Đang chuẩn bị chuyển tiếp…"; _progress.Value = 0;
        _working = true; _retry.Enabled = _close.Enabled = false; _cancel.Enabled = true;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        try
        {
            var report = new Progress<ForwardProgress>(p =>
            {
                if (IsDisposed || !_working || _operation.IsCancellationRequested) return;
                _progress.Value = p.Total == 0 ? 0 : Math.Min(1000, p.Completed * 1000 / p.Total);
                _state.Text = $"{p.Completed}/{p.Total} · {p.Destination}" + (p.File is { } f ? " · " + AttachmentProgressText.Describe(f) : "");
            });
            var result = await Task.Run(() => _chat.ForwardBatchAsync(requests, report, _operation.Token));
            foreach (var item in result) _results[item.Request] = item;
        }
        catch (Exception ex)
        {
            foreach (var request in requests)
                if (!_results.TryGetValue(request, out var saved) || saved.Outcome != ForwardOutcome.Queued)
                    _results[request] = new(request, _operation.IsCancellationRequested ? ForwardOutcome.Cancelled : ForwardOutcome.Failed, Error: ex.Message);
        }
        finally
        {
            _working = false; _operation.Dispose(); _operation = null;
            _close.Enabled = true; _cancel.Enabled = false;
            _retry.Enabled = _requests.Any(r => !_results.TryGetValue(r, out var result) || result.Outcome != ForwardOutcome.Queued);
            ShowResults();
        }
    }
    private void ShowResults()
    {
        var messageOrder = _requests.Select(r => r.MessageId).Distinct().ToList();
        _items.BeginUpdate(); _items.Items.Clear();
        foreach (var request in _requests)
        {
            _results.TryGetValue(request, out var result);
            var text = result?.Outcome switch { ForwardOutcome.Queued => "Đã lưu chờ gửi", ForwardOutcome.Cancelled => "Đã hủy", _ => "Lỗi: " + result?.Error };
            _items.Items.Add(new ListViewItem([request.Destination.Name, (messageOrder.IndexOf(request.MessageId) + 1).ToString(), text]));
        }
        _items.EndUpdate();
        var saved = _results.Values.Count(r => r.Outcome == ForwardOutcome.Queued);
        _state.Text = $"Đã lưu chờ gửi {saved}/{_requests.Length}. Thử lại chỉ gửi các mục chưa hoàn tất; không gửi lại mục thành công.";
        _progress.Value = _requests.Length == 0 ? 0 : saved * 1000 / _requests.Length;
    }
}
