using LanChat.Infrastructure;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms.Forms;

internal sealed class NetworkManagerForm : Form
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, DisplayMember = nameof(NetworkEntry.Name), AccessibleName = "Mạng đã lưu" };
    private readonly TextBox _codes = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "Mã tham gia, mỗi mã một dòng" };
    private readonly Label _status = Theme.Label("");
    private bool _busy;

    public NetworkManagerForm(NetworkCatalog catalog, Func<NetworkEntry, bool, Task> connect, Func<NetworkEntry, Task> remove,
        Func<NetworkEntry, Task> stop, Action<NetworkEntry, Form> details, Func<Guid, bool> connected, Func<Task> restoreDefault, Func<Guid?> selected, Action<Guid> selectNetwork)
    {
        Icon = AppIcon.Value;
        Text = "Mạng chat"; ClientSize = new Size(750, 408); MinimumSize = new Size(720, 385);
        Font = new Font("Segoe UI", 10); StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.Dpi;
        var layout = Theme.Stack(6); layout.Padding = new Padding(12);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 51));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.Controls.Add(Theme.Label("Mạng đã lưu", 10, true), 0, 0); layout.Controls.Add(_list, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var show = Theme.Secondary("Chi tiết");
        var retry = Theme.Secondary("Kết nối lại");
        var rename = Theme.Secondary("Đổi tên");
        var delete = Theme.Secondary("Gỡ mạng");
        var restore = Theme.Secondary("Khôi phục mạng mặc định");
        actions.Controls.AddRange([show, retry, rename, delete, restore]); layout.Controls.Add(actions, 0, 2);
        layout.Controls.Add(Theme.Label("Mã tham gia (mỗi mã một dòng)"), 0, 3); layout.Controls.Add(_codes, 0, 4);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var add = Theme.Button("Thêm mã");
        var create = Theme.Secondary("Tạo mạng");
        bottom.Controls.Add(add, 0, 0); bottom.Controls.Add(create, 1, 0); bottom.Controls.Add(_status, 2, 0);
        layout.Controls.Add(bottom, 0, 5); Controls.Add(layout);

        void UpdateButtons()
        {
            var item = _list.SelectedItem as NetworkEntry;
            show.Enabled = item is not null && connected(item.Id);
            retry.Enabled = item is not null && !connected(item.Id);
            rename.Enabled = delete.Enabled = item is not null;
            restore.Visible = !catalog.Entries.Any(catalog.IsDefault);
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
            _busy = true; layout.Enabled = false;
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally { _busy = false; layout.Enabled = true; UpdateButtons(); }
        }
        Shown += async (_, _) => await RunAsync(OfferToRemoveDefaultAsync);
        async Task AddAsync(bool creator)
        {
            var codes = creator ? new[] { ClusterConfigurationStore.CreateInvitation() } : _codes.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var added = await catalog.AddAsync(codes, creator);
            if (!creator) _codes.Clear();
            RefreshList(added[0].Id);
            var errors = new List<string>();
            foreach (var item in added)
                try { await connect(item, false); }
                catch (Exception ex) { errors.Add(item.Name + " · " + ex.Message); }
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
            await catalog.ConfirmFirstPrivateCodeAsync(removeDefault);
            if (removeDefault) await stop(defaultNetwork!);
            RefreshList((_list.SelectedItem as NetworkEntry)?.Id);
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
        rename.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_list.SelectedItem is not NetworkEntry item) return;
            var name = SelectionForm.Prompt(this, "Tên mạng chat", item.Name);
            if (name is null) return;
            await catalog.RenameAsync(item, name);
            RefreshList(item.Id); _status.Text = "Đã đổi tên.";
        });
        delete.Click += async (_, _) => await RunAsync(async () =>
        {
            if (_list.SelectedItem is not NetworkEntry item) return;
            if (MessageBox.Show(this, $"Gỡ {item.Name} khỏi danh sách? Bạn có thể dùng lại mã để mở lịch sử đã lưu.", "Gỡ mạng",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            await remove(item);
            RefreshList(null); _status.Text = "Đã gỡ mạng.";
        });
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };
        RefreshList(selected());
    }
}
