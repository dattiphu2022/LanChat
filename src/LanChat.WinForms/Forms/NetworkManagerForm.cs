using LanChat.Infrastructure;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed class NetworkManagerForm : DpiForm
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, DisplayMember = nameof(NetworkEntry.Name), AccessibleName = "Mạng đã lưu" };
    private readonly PromptTextBox _codes = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
        AccessibleName = "Mã tham gia, mỗi mã một dòng", PlaceholderText = "Dán mã mời nhận từ máy khác, mỗi mã một dòng." };
    private readonly Label _status = Theme.Label("");
    private bool _busy;

    public NetworkManagerForm(NetworkCatalog catalog, Func<NetworkEntry, bool, Task> connect, Func<NetworkEntry, Task> remove,
        Action<NetworkEntry, Form> details, Func<Guid, bool> connected, Func<Task> restoreDefault, Func<Guid?> selected, Action<Guid> selectNetwork)
    {
        Icon = AppIcon.Value;
        Text = "Mạng chat"; ClientSize = new Size(780, 520); MinimumSize = new Size(620, 400);
        StartPosition = FormStartPosition.CenterParent;
        var layout = Theme.Stack(7); layout.Padding = new Padding(12);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Theme.Label("Mạng đã lưu", 10, true), 0, 0); layout.Controls.Add(_list, 0, 1);
        var actions = Theme.Toolbar();
        var show = Theme.Secondary("Chi tiết", UiIcon.Info);
        var retry = Theme.Secondary("Kết nối lại", UiIcon.Refresh);
        var rename = Theme.Secondary("Đổi tên", UiIcon.Edit);
        var delete = Theme.Secondary("Gỡ mạng", UiIcon.Delete);
        var restore = Theme.Secondary("Khôi phục mạng mặc định", UiIcon.Restore);
        actions.Controls.AddRange([show, retry, rename, delete, restore]); layout.Controls.Add(actions, 0, 2);
        layout.Controls.Add(Theme.Label("Mã tham gia (mỗi mã một dòng)"), 0, 3); layout.Controls.Add(_codes, 0, 4);
        var bottom = Theme.Toolbar();
        var add = Theme.Button("Thêm mã", UiIcon.Add);
        var create = Theme.Secondary("Tạo mạng", UiIcon.AddNetwork);
        bottom.Controls.AddRange([add, create, UiInteraction.CloseButton(this)]);
        layout.Controls.Add(bottom, 0, 5); layout.Controls.Add(_status, 0, 6); Controls.Add(layout);
        UiInteraction.Wrap(_status, layout);

        void UpdateButtons()
        {
            var item = _list.SelectedItem as NetworkEntry;
            show.Enabled = item is not null && connected(item.Id);
            retry.Enabled = item is not null && !connected(item.Id);
            rename.Enabled = delete.Enabled = item is not null;
            restore.Visible = catalog.CanRestoreDefaultNetwork && !catalog.Entries.Any(catalog.IsDefault);
            add.Enabled = !string.IsNullOrWhiteSpace(_codes.Text);
        }
        void RefreshList(Guid? id)
        {
            _list.Items.Clear(); _list.Items.AddRange(catalog.Entries.Cast<object>().ToArray());
            if (id.HasValue) _list.SelectedItem = catalog.Entries.FirstOrDefault(e => e.Id == id);
            if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
            UpdateButtons();
        }
        async Task RunAsync(Func<Task> action)
        {
            if (_busy) return;
            _busy = true; actions.Enabled = bottom.Enabled = _list.Enabled = _codes.Enabled = false;
            _status.Text = "Đang xử lý…";
            try { await action(); }
            catch (OperationCanceledException) { _status.Text = "Đã hủy thao tác."; }
            catch (Exception ex) { _status.Text = UiText.HideIpAddresses(ex.Message); }
            finally { _busy = false; if (!IsDisposed) { actions.Enabled = bottom.Enabled = _list.Enabled = _codes.Enabled = true; UpdateButtons(); } }
        }
        Shown += async (_, _) => { if (catalog.NeedsDefaultChoice && catalog.Entries.Any(e => !catalog.IsDefault(e) && connected(e.Id))) await RunAsync(OfferToRemoveDefaultAsync); };
        _codes.TextChanged += (_, _) => UpdateButtons();
        async Task AddAsync(bool creator)
        {
            var codes = creator ? new[] { ClusterConfigurationStore.CreateInvitation() } : _codes.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var added = await catalog.AddAsync(codes, creator);
            if (!creator) _codes.Clear();
            RefreshList(added[0].Id);
            var errors = new List<string>();
            foreach (var item in added)
                try { await connect(item, false); }
                catch (Exception ex) { errors.Add(item.Name + " · " + UiText.HideIpAddresses(ex.Message)); }
            var connectedNetwork = added.FirstOrDefault(item => connected(item.Id));
            if (connectedNetwork is not null)
            {
                await OfferToRemoveDefaultAsync();
                selectNetwork(connectedNetwork.Id);
            }
            _status.Text = errors.Count == 0 ? $"Đã thêm {added.Length} mạng." : string.Join("  |  ", errors);
        }
        async Task OfferToRemoveDefaultAsync()
        {
            if (!catalog.NeedsDefaultChoice || !catalog.Entries.Any(e => !catalog.IsDefault(e) && connected(e.Id))) return;
            var defaultNetwork = catalog.Entries.FirstOrDefault(catalog.IsDefault);
            var removeDefault = defaultNetwork is not null && MessageBox.Show(this,
                "Đã kết nối mạng riêng đầu tiên. Bạn có muốn gỡ mạng mặc định? Lịch sử của mạng vẫn được lưu trên máy.",
                "Mạng mặc định", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            if (removeDefault) await remove(defaultNetwork!);
            await catalog.ConfirmFirstPrivateCodeAsync(false);
            RefreshList((_list.SelectedItem as NetworkEntry)?.Id);
            _status.Text = removeDefault ? "Đã gỡ mạng mặc định." : "Đã giữ mạng mặc định.";
        }
        restore.Click += async (_, _) => await RunAsync(async () =>
        {
            await restoreDefault();
            RefreshList(catalog.DefaultNetwork?.Id);
            _status.Text = "Đã mở lại mạng mặc định.";
        });
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        add.Click += async (_, _) => await RunAsync(() => AddAsync(false));
        create.Click += async (_, _) => await RunAsync(() => AddAsync(true));
        show.Click += (_, _) => { if (_list.SelectedItem is NetworkEntry item) details(item, this); };
        retry.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_list.SelectedItem is NetworkEntry item) { await connect(item, true); await OfferToRemoveDefaultAsync(); _status.Text = "Đã kết nối."; }
        });
        rename.Click += async (_, _) => await RunAsync(() =>
        {
            if (_list.SelectedItem is not NetworkEntry item) return Task.CompletedTask;
            var name = SelectionForm.Prompt(this, "Tên mạng chat", "Ví dụ: Văn phòng chính", item.Name, maxLength: 60,
                save: value => catalog.RenameAsync(item, value));
            if (name is null) { _status.Text = "Đã hủy đổi tên."; return Task.CompletedTask; }
            RefreshList(item.Id); _status.Text = "Đã đổi tên.";
            return Task.CompletedTask;
        });
        delete.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_list.SelectedItem is not NetworkEntry item) return;
            if (MessageBox.Show(this, $"Gỡ {item.Name} khỏi danh sách? Bạn có thể dùng lại mã để mở lịch sử đã lưu.", "Gỡ mạng",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) { _status.Text = "Đã hủy gỡ mạng."; return; }
            await remove(item);
            RefreshList(null); _status.Text = "Đã gỡ mạng.";
        });
        FormClosing += (_, e) =>
        {
            if (_busy) { e.Cancel = true; _status.Text = "Đang xử lý mạng chat. Vui lòng chờ hoàn tất."; }
            else if (!string.IsNullOrWhiteSpace(_codes.Text) && !UiInteraction.Discard(this, "Bỏ các mã tham gia chưa thêm?")) e.Cancel = true;
        };
        RefreshList(selected());
    }
}
