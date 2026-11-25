using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed record TransferEntry(AttachmentDto File, string Conversation);

// This view observes the shared preparation coordinator. Closing it never cancels a transfer.
internal sealed class TransferCenterForm : DpiForm
{
    private readonly AttachmentActions _actions;
    private readonly CancellationToken _networkLifetime;
    private readonly CancellationTokenSource _viewLifetime = new();
    private readonly Func<CancellationToken, Task<TransferEntry[]>> _load;
    private readonly Func<AttachmentDto, Task>? _openAttachment;
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Theme.Surface, BorderStyle = BorderStyle.None,
        AccessibleName = "Tệp và tiến độ truyền trong mạng hiện tại" };
    private readonly PromptTextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "Tìm tên tệp hoặc hội thoại…", AccessibleName = "Tìm tệp truyền" };
    private readonly Label _status = Theme.Label("Đang tải danh sách…", 9);
    private readonly Button _open = Theme.Secondary("Mở", UiIcon.Open);
    private readonly Button _save = Theme.Secondary("Lưu về máy", UiIcon.Save);
    private readonly Button _retry = Theme.Button("Chuẩn bị / Thử lại", UiIcon.Refresh);
    private readonly Dictionary<Guid, DataGridViewRow> _rows = [];
    private readonly Dictionary<Guid, long> _requests = [];
    private readonly HashSet<Guid> _watched = [];
    private readonly HashSet<Guid> _pending = [];
    private void PollPending()
    {
        if (_stopped) return;
        foreach (var id in _pending.Intersect(_watched).ToArray()) Changed(id);
        foreach (var id in _watched.ToArray())
            if (_rows.TryGetValue(id, out var row)) _ = UpdateFolderSizeAsync(row);
    }
    private bool _readingFolder;
    private bool _loading, _stopped;
    private TransferEntry? Selected => _grid.CurrentRow?.Tag as TransferEntry;

    internal TransferCenterForm(AttachmentActions actions, CancellationToken networkLifetime, Func<CancellationToken, Task<TransferEntry[]>> load, Func<AttachmentDto, Task>? openAttachment = null)
    {
        _actions = actions; _networkLifetime = networkLifetime; _load = load; _openAttachment = openAttachment;
        Text = "Tệp · Trung tâm truyền"; Icon = AppIcon.Value; ClientSize = new Size(900, 570);
        MinimumSize = new Size(540, 360); StartPosition = FormStartPosition.CenterParent;
        var root = Theme.Stack(4); root.Padding = new Padding(12);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (var (name, weight) in new[] { ("Tệp / thư mục",32f), ("Hội thoại",23f), ("Trạng thái",30f), ("Kích thước",15f) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.DefaultCellStyle.Font = Theme.GetFont(9,false); _grid.DefaultCellStyle.ForeColor = Theme.Ink;
        _grid.DefaultCellStyle.SelectionBackColor = Theme.Selection; _grid.DefaultCellStyle.SelectionForeColor = Theme.Ink;
        _grid.RowTemplate.Height = 32; _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.CellToolTipTextNeeded += (_,e) => { if (e.RowIndex>=0) e.ToolTipText = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex<0?0:e.ColumnIndex].Value?.ToString(); };
        _grid.SelectionChanged += (_,_) => UpdateActions(); _grid.Scroll += (_,_) => WatchVisible();
        _grid.SizeChanged += (_,_) => WatchVisible(); _grid.CellDoubleClick += async (_,e) => { if(e.RowIndex>=0) await OpenAsync(); };
        _search.TextChanged += (_,_) => Filter();
        _open.Click += async (_,_) => await OpenAsync();
        _save.Click += async (_,_) => { if (Selected is { } entry) await _actions.SaveAsync(Owner ?? this, entry.File, _networkLifetime); };
        _retry.Click += async (_,_) => { if (Selected is { } entry) await _actions.PrepareAsync(entry.File, _networkLifetime); };
        var refresh = Theme.Secondary("Làm mới",UiIcon.Refresh); refresh.Click += async (_,_) => await RefreshMetadataAsync();
        var close = UiInteraction.CloseButton(this); close.Click += (_,_) => Close();
        var buttons = Theme.Toolbar(); buttons.Controls.AddRange([_open,_save,_retry,refresh,close]);
        UiInteraction.Wrap(_status,root); _status.AccessibleRole = AccessibleRole.StatusBar;
        root.Controls.Add(_search,0,0); root.Controls.Add(_grid,0,1); root.Controls.Add(_status,0,2); root.Controls.Add(buttons,0,3); Controls.Add(root);
        _actions.Changed += Changed; _actions.PollIdle += PollPending;
        Shown += async (_,_) => { FitGridDpi(); await RefreshMetadataAsync(); };
        Disposed += (_,_) =>
        {
            if (_stopped) return;
            _stopped=true; _actions.Changed-=Changed; _actions.PollIdle-=PollPending;
            foreach (var id in _watched) _actions.Unwatch(id);
            _watched.Clear(); _viewLifetime.Cancel(); _viewLifetime.Dispose();
        };
        UpdateActions();
    }
    private void FitGridDpi()
    {
        _grid.DefaultCellStyle.Font = Theme.GetFont(9 * this.LayoutDpi() / 96f, false);
        var height = Math.Max(this.ScalePixels(32), _grid.DefaultCellStyle.Font.Height + this.ScalePixels(10));
        _grid.RowTemplate.Height = height; foreach (DataGridViewRow row in _grid.Rows) row.Height = height;
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); FitGridDpi(); }
    private async Task OpenAsync()
    {
        if (Selected is not { } entry) return;
        if (_openAttachment is not null) await _openAttachment(entry.File);
        else await _actions.OpenAsync(Owner ?? this,entry.File,_networkLifetime);
    }
    internal async Task RefreshMetadataAsync()
    {
        if(_loading || _stopped) return; _loading=true;
        try
        {
            var entries=await _load(_viewLifetime.Token); if(_stopped) return;
            var ids=new HashSet<Guid>(entries.Select(e=>e.File.Id));
            foreach(var id in _rows.Keys.Where(id=>!ids.Contains(id)).ToArray()) { _grid.Rows.Remove(_rows[id]); _rows.Remove(id); _requests.Remove(id); _pending.Remove(id); }
            foreach(var entry in entries)
            {
                if(!_rows.TryGetValue(entry.File.Id,out var row))
                {
                    row=_grid.Rows[_grid.Rows.Add(entry.File.FileName,entry.Conversation,"Đang kiểm tra…",InitialSize(entry.File))];
                    _rows.Add(entry.File.Id,row);
                }
                if (row.Tag is TransferEntry previous && previous.File != entry.File)
                {
                    Set(row,3,InitialSize(entry.File));
                    row.Cells[3].Tag = null;
                }
                row.Tag=entry; Set(row,0,entry.File.FileName); Set(row,1,entry.Conversation);
            }
            Filter(); foreach (var id in _watched.ToArray()) Changed(id);
            _status.Text=entries.Length==0 ? "Chưa có tệp trong mạng này." :
                $"{entries.Length} tệp / thư mục · Đóng cửa sổ vẫn tiếp tục tải nền.";
        }
        catch(OperationCanceledException) { }
        catch(Exception) { if(!_stopped) _status.Text="Chưa tải được danh sách."; }
        finally { _loading=false; }
    }
    private void Filter()
    {
        if(_stopped) return;
        var selected = Selected?.File.Id; var top = _grid.FirstDisplayedScrollingRowIndex;
        _grid.CurrentCell=null; var query=_search.Text.Trim();
        foreach(var row in _rows.Values)
            row.Visible = query.Length==0 || MentionDraft.Matches(((TransferEntry)row.Tag).File.FileName+" "+((TransferEntry)row.Tag).Conversation,query);
        if (selected is Guid id && _rows.TryGetValue(id,out var active) && active.Visible) _grid.CurrentCell=active.Cells[0];
        if (top>=0 && top<_grid.Rows.Count && _grid.Rows[top].Visible) _grid.FirstDisplayedScrollingRowIndex=top;
        WatchVisible(); UpdateActions();
    }
    private void WatchVisible()
    {
        if(_stopped || !_grid.IsHandleCreated) return;
        var visible=_rows.Where(x=>x.Value.Visible && x.Value.Displayed).Select(x=>x.Key).ToHashSet();
        foreach(var id in _watched.Except(visible).ToArray()) { _actions.Unwatch(id); _watched.Remove(id); }
        foreach(var id in visible.Except(_watched).ToArray()) { _actions.Watch(id); _watched.Add(id); Changed(id); }
    }
    private async void Changed(Guid id)
    {
        if(_stopped || !_rows.TryGetValue(id,out var row) || !_watched.Contains(id)) return;
        _ = UpdateFolderSizeAsync(row);
        _requests.TryGetValue(id,out var request); _requests[id]=++request;
        var file=((TransferEntry)row.Tag).File; var progress=_actions.Progress(id);
        if(_actions.IsBusy(id) || progress is not null) Set(row,2,_actions.IsBusy(id)?_actions.State(id):AttachmentProgressText.Describe(progress!));
        else
        {
            try
            {
                var local=await _actions.LocationAsync(file,_viewLifetime.Token);
                if(_stopped || !_requests.TryGetValue(id,out var current) || current!=request || !_rows.TryGetValue(id,out var currentRow) || !ReferenceEquals(row,currentRow)) return;
                if (local.Exists) _pending.Remove(id); else _pending.Add(id);
                Set(row,2,local.Exists?"Hoàn thành · sẵn sàng mở":local.UserSelected?"Không tìm thấy bản đã lưu":_actions.Failure(id) is { } error ? error : "Chờ chuẩn bị");
            }
            catch(OperationCanceledException) { }
            catch(Exception) { if(!_stopped && _requests.TryGetValue(id,out var current) && current==request && _watched.Contains(id)) Set(row,2,"Chưa kiểm tra được"); }
        }
        if(!_stopped) UpdateActions();
    }
    private static string InitialSize(AttachmentDto file) =>
        file.MimeType == FolderAttachment.MimeType ? "—" : $"{file.FileSize / 1024d:N1} KB";

    private async Task UpdateFolderSizeAsync(DataGridViewRow row)
    {
        if (_stopped || _readingFolder || !Equals(row.Cells[3].Value, "—") ||
            row.Tag is not TransferEntry { File.MimeType: FolderAttachment.MimeType } entry) return;
        var now = DateTime.UtcNow;
        if (row.Cells[3].Tag is DateTime retryAt && now < retryAt) return;
        row.Cells[3].Tag = now.AddSeconds(30);
        _readingFolder = true;
        try
        {
            var summary = await _actions.FolderSummaryAsync(entry.File, _viewLifetime.Token);
            if (_stopped || row.DataGridView != _grid || !Equals(row.Tag, entry)) return;
            if (summary is { } value)
                Set(row, 3, $"{value.TotalBytes / 1024d:N1} KB · {value.FileCount:N0} tệp");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or System.Text.Json.JsonException) { }
        finally { _readingFolder = false; }
    }

    private void UpdateActions()
    {
        var selected=Selected; _open.Enabled=_save.Enabled=_retry.Enabled=selected is not null && !_actions.IsBusy(selected.File.Id);
    }
    private static void Set(DataGridViewRow row,int column,string value) { if(!Equals(row.Cells[column].Value,value)) row.Cells[column].Value=value; }
}
