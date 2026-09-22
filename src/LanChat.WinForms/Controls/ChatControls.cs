using LanChat.Domain;
using LanChat.Application;
using ChatMessage = LanChat.Domain.Message;

namespace LanChat.WinForms.Controls;

public sealed class MessageBubbleControl : UserControl
{
    private readonly Label _body = Theme.Label("");
    private readonly Label _state = Theme.Label("", 8);
    private readonly FlowLayoutPanel _files = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
    private readonly ContextMenuStrip _menu = new();
    private ChatMessage? _message;
    private bool _own;
    private MessageDetails? _details;
    public event Action<ChatMessage, string>? ActionRequested;
    public event Action<AttachmentDto>? FileRequested;
    public MessageBubbleControl()
    {
        Size = new Size(400, 120); Padding = new Padding(12, 8, 12, 8);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        _body.AutoSize = false; _state.AutoSize = false;
        layout.Controls.Add(_body, 0, 0); layout.Controls.Add(_files, 0, 1); layout.Controls.Add(_state, 0, 2); Controls.Add(layout);
        foreach (var (caption, action) in new[] { ("Trả lời", "reply"), ("Sao chép", "copy"), ("Chuyển tiếp", "forward"), ("Sửa", "edit"), ("Thu hồi", "recall"), ("Xóa phía tôi", "delete") })
        {
            var item = _menu.Items.Add(caption); item.Tag = action;
            item.Click += (_, _) => { if (_message is not null) { if (action == "copy") { if (_message.Text.Length > 0) Clipboard.SetText(_message.Text); } else ActionRequested?.Invoke(_message, action); } };
        }
        var reactions = new ToolStripMenuItem("Cảm xúc"); reactions.Tag = "react";
        foreach (var emoji in new[] { "👍", "❤️", "😂", "😮", "😢" }) reactions.DropDownItems.Add(emoji, null, (_, _) => { if (_message is not null) ActionRequested?.Invoke(_message, "react:" + emoji); });
        reactions.DropDownItems.Add("Gỡ cảm xúc của tôi", null, (_, _) => { if (_message is not null) ActionRequested?.Invoke(_message, "unreact"); });
        _menu.Items.Add(reactions);
        _menu.Opening += (_, _) => { foreach (ToolStripItem item in _menu.Items) item.Enabled = _message is not null && _message.Kind != MessageKind.System && ((string?)item.Tag == "delete" || (!_message.IsRecalled && ((string?)item.Tag is not ("edit" or "recall") || _own))); };
        ContextMenuStrip = _body.ContextMenuStrip = _state.ContextMenuStrip = _menu;
        Resize += (_, _) => UpdateHeight();
    }
    private void UpdateHeight()
    {
        var measured = TextRenderer.MeasureText(_body.Text, _body.Font, new Size(Math.Max(100, Width - Padding.Horizontal - 40), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
        var height = measured.Height + Padding.Vertical + 62 + (_files.Visible ? 48 : 0);
        if (Height != height) Height = height;
    }
    public void Bind(ChatMessage m, bool own)
    {
        _message = m; _own = own;
        _body.Text = (!own && m.RecipientUserId == Guid.Empty && _details?.SenderName is { } sender ? sender + "\n" : "") + (_details?.ReplyPreview is { } reply ? "↪ " + reply.ReplaceLineEndings(" ") + "\n" : "") + (m.ForwardedFromMessageId.HasValue ? "↗ Đã chuyển tiếp\n" : "") + m.Text;
        _state.Text = new DateTime(m.CreatedAtTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm") +
            (m.IsEdited ? " · Đã sửa" : "") + (own ? m.Status switch { MessageStatus.Delivered => "  ✓✓ Đã nhận", MessageStatus.Synced => "  ✓ Đã đồng bộ", _ => "  ◷ Chưa đồng bộ" } : "") +
            (_details is { ReadCount: > 0 } d ? $" · {d.ReadCount} đã đọc" : "") + (_details is null ? "" : "  " + string.Join("  ", _details.Reactions));
        _state.ForeColor = Theme.Muted;
        Controls[0].BackColor = m.Kind == MessageKind.System ? Theme.Canvas : own ? Color.FromArgb(221, 236, 255) : Color.White;
        _body.TextAlign = own ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
        _state.TextAlign = _body.TextAlign;
        Padding = own ? new Padding(60, 6, 14, 6) : new Padding(14, 6, 60, 6);
        UpdateHeight();
    }
    public void BindDetails(MessageDetails details)
    {
        var previous = _details; _details = details;
        var files = details.Message.IsRecalled ? [] : details.Attachments;
        if (previous is null || !previous.Attachments.SequenceEqual(files) || previous.Message.IsRecalled != details.Message.IsRecalled)
        {
            foreach (Control c in _files.Controls.Cast<Control>().ToArray()) c.Dispose();
            foreach (var file in files)
            {
                var link = new LinkLabel { Text = $"{(file.MimeType.StartsWith("image/") ? "▧" : "▤")} {file.FileName} ({file.FileSize / 1024d:N1} KB)", AutoSize = true, Margin = new Padding(4, 10, 12, 4) };
                link.LinkClicked += (_, _) => FileRequested?.Invoke(file); _files.Controls.Add(link);
            }
        }
        _files.Visible = files.Length > 0; ((TableLayoutPanel)Controls[0]).RowStyles[1].Height = _files.Visible ? 48 : 0;
        Bind(details.Message, _own);
    }
}

public sealed class MessageListControl : UserControl
{
    private readonly FlowLayoutPanel _flow = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Canvas };
    private readonly Dictionary<Guid, MessageBubbleControl> _bubbles = [];
    private Guid? _conversation;
    private bool _resizing;
    public event Action<ChatMessage, string>? ActionRequested;
    public event Action<AttachmentDto>? FileRequested;
    public MessageListControl()
    {
        Dock = DockStyle.Fill; Controls.Add(_flow);
        _flow.ClientSizeChanged += (_, _) => ResizeItems();
    }
    private void ResizeItems()
    {
        if (_resizing) return;
        _resizing = true;
        try
        {
            var scroll = _flow.AutoScrollPosition;
            _flow.AutoScroll = false;
            foreach (var bubble in _bubbles.Values) bubble.Width = Math.Max(180, _flow.ClientSize.Width - 28);
            _flow.AutoScroll = true;
            _flow.AutoScrollPosition = new Point(0, -scroll.Y);
        }
        finally { _resizing = false; }
    }
    public void Bind(Guid conversation, IReadOnlyList<ChatMessage> messages, Guid localUser)
    {
        var changedConversation = _conversation != conversation;
        var nearBottom = _flow.VerticalScroll.Value + _flow.ClientSize.Height >= _flow.VerticalScroll.Maximum - 60;
        if (changedConversation) { foreach (Control c in _flow.Controls.Cast<Control>().ToArray()) c.Dispose(); _bubbles.Clear(); _conversation = conversation; }
        var newMessage = false;
        _flow.SuspendLayout();
        foreach (var id in _bubbles.Keys.Except(messages.Select(x => x.Id)).ToArray()) { _bubbles[id].Dispose(); _bubbles.Remove(id); }
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (!_bubbles.TryGetValue(m.Id, out var bubble))
            {
                bubble = new MessageBubbleControl(); bubble.ActionRequested += (m, a) => ActionRequested?.Invoke(m, a); bubble.FileRequested += f => FileRequested?.Invoke(f);
                _bubbles.Add(m.Id, bubble); _flow.Controls.Add(bubble); newMessage = true;
            }
            bubble.Bind(m, m.SenderUserId == localUser); _flow.Controls.SetChildIndex(bubble, i);
        }
        ResizeItems(); _flow.ResumeLayout(true);
        if ((changedConversation || nearBottom) && newMessage && messages.Count > 0) _flow.ScrollControlIntoView(_bubbles[messages[^1].Id]);
    }
    public void BindDetails(IEnumerable<MessageDetails> details) { foreach (var d in details) if (_bubbles.TryGetValue(d.Message.Id, out var bubble)) bubble.BindDetails(d); }
    public void FocusMessage(Guid id) { if (_bubbles.TryGetValue(id, out var bubble)) { _flow.ScrollControlIntoView(bubble); bubble.BorderStyle = BorderStyle.FixedSingle; } }
}

public sealed record ComposerSubmission(string Text, string[] Files, Guid? ReplyTo, Guid? EditId);

public sealed class ChatInputControl : UserControl
{
    private readonly TextBox _text = new() { Multiline = true, Dock = DockStyle.Fill, MaxLength = 16000, ScrollBars = ScrollBars.Vertical,
        PlaceholderText = "Nhập tin nhắn…  Enter để gửi · Shift+Enter để xuống dòng" };
    private readonly Button _send = Theme.Button("Gửi ➤");
    private readonly FlowLayoutPanel _previews = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
    private readonly LinkLabel _context = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly List<string> _paths = [];
    private Guid? _replyTo, _editId;
    public event Func<ComposerSubmission, Task>? SendRequested;
    public event Action<int>? HeightRequested;
    public ChatInputControl()
    {
        Dock = DockStyle.Fill; Padding = new Padding(12); BackColor = Color.White;
        var root = Theme.Stack(3); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        var attach = Theme.Button("＋"); attach.MinimumSize = new Size(50, 36); layout.Controls.Add(attach, 0, 0);
        layout.Controls.Add(_text, 1, 0); layout.Controls.Add(_send, 2, 0);
        root.Controls.Add(_context, 0, 0); root.Controls.Add(_previews, 0, 1); root.Controls.Add(layout, 0, 2); Controls.Add(root);
        attach.Click += (_, _) => { using var dialog = new OpenFileDialog { Multiselect = true, Title = "Chọn tệp đính kèm" }; if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles(dialog.FileNames); };
        _context.LinkClicked += (_, _) => ResetContext();
        InstallDrop(this); InstallDrop(_text); InstallDrop(_previews);
        _send.Click += async (_, _) => await SendAsync();
        _text.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; await SendAsync(); } };
    }
    private async Task SendAsync()
    {
        if (!_send.Enabled || (string.IsNullOrWhiteSpace(_text.Text) && _paths.Count == 0) || SendRequested is null) return;
        Enabled = false; _send.Enabled = false; _text.ReadOnly = true;
        try { await SendRequested(new(_text.Text, _paths.ToArray(), _replyTo, _editId)); _text.Clear(); _paths.Clear(); RefreshPreviews(); ResetContext(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Chưa gửi được", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { Enabled = true; _send.Enabled = true; _text.ReadOnly = false; _text.Focus(); }
    }
    public void InstallDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) => e.Effect = Enabled && !_editId.HasValue && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        control.DragDrop += (_, e) => { if (Enabled && e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths); };
    }
    public void AddFiles(IEnumerable<string> paths)
    {
        if (_editId.HasValue) return;
        foreach (var path in paths.Where(File.Exists)) if (!_paths.Contains(path, StringComparer.OrdinalIgnoreCase) && _paths.Count < 20) _paths.Add(path);
        RefreshPreviews();
    }
    private void RefreshPreviews()
    {
        foreach (Control c in _previews.Controls.Cast<Control>().ToArray()) c.Dispose();
        foreach (var path in _paths.ToArray()) { var preview = new AttachmentPreviewControl(path); preview.RemoveRequested += () => { _paths.Remove(path); RefreshPreviews(); }; _previews.Controls.Add(preview); }
        UpdateLayoutHeight();
    }
    private void UpdateLayoutHeight()
    {
        var root = (TableLayoutPanel)Controls[0]; root.RowStyles[0].Height = _context.Text.Length > 0 ? 26 : 0; root.RowStyles[1].Height = _paths.Count > 0 ? 82 : 0;
        HeightRequested?.Invoke(110 + (int)root.RowStyles[0].Height + (int)root.RowStyles[1].Height);
    }
    public void SetContext(ChatMessage message, bool edit)
    {
        _replyTo = edit ? null : message.Id; _editId = edit ? message.Id : null;
        _context.Text = (edit ? "Sửa: " : "Trả lời: ") + message.Text.ReplaceLineEndings(" ") + "  · Bấm để hủy";
        if (edit) { _paths.Clear(); RefreshPreviews(); _text.Text = message.Text; }
        UpdateLayoutHeight(); _text.Focus();
    }
    public void ResetContext() { if (_editId.HasValue) _text.Clear(); _replyTo = _editId = null; _context.Text = ""; UpdateLayoutHeight(); }
    public void ResetDraft() { _text.Clear(); _paths.Clear(); RefreshPreviews(); ResetContext(); }
#if DEBUG
    internal async Task SubmitForVerificationAsync(string text) { _text.Text = text; await SendAsync(); }
    internal void DropForVerification(string[] files)
    {
        var data = new DataObject(DataFormats.FileDrop, files);
        var args = new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
        OnDragEnter(args); if (args.Effect != DragDropEffects.Copy) throw new InvalidOperationException("ChatInput không nhận kéo thả tệp.");
        OnDragDrop(args); if (_paths.Count != files.Length) throw new InvalidOperationException("Thiếu tệp trong preview.");
    }
#endif
}

public sealed class ChatViewControl : UserControl
{
    private readonly Label _title = Theme.Label("Chào mừng đến LanChat", 17, true);
    private readonly Label _subtitle = Theme.Label("Chọn một người trong mạng nội bộ để bắt đầu trò chuyện.");
    private readonly MessageListControl _messages = new();
    private readonly ChatInputControl _input = new() { Enabled = false };
    private readonly LinkLabel _older = new() { Text = "Xem tin nhắn cũ hơn", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    public event Func<ComposerSubmission, Task>? SendRequested;
    public event Func<Task>? OlderRequested;
    public event Action<ChatMessage, string>? ActionRequested;
    public event Action<AttachmentDto>? FileRequested;
    public ChatViewControl()
    {
        Dock = DockStyle.Fill;
        var layout = Theme.Stack(5);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        _title.Padding = new Padding(14, 8, 0, 0); _subtitle.Padding = new Padding(14, 0, 0, 0); _subtitle.ForeColor = Theme.Muted;
        _title.AutoSize = false; _title.AutoEllipsis = true; _subtitle.AutoSize = false; _subtitle.AutoEllipsis = true;
        layout.Controls.Add(_title, 0, 0); layout.Controls.Add(_subtitle, 0, 1); layout.Controls.Add(_older, 0, 2); layout.Controls.Add(_messages, 0, 3); layout.Controls.Add(_input, 0, 4);
        Controls.Add(layout); _input.SendRequested += async text => { if (SendRequested is not null) await SendRequested(text); };
        _input.HeightRequested += height => layout.RowStyles[4].Height = height;
        _messages.ActionRequested += (m, a) => ActionRequested?.Invoke(m, a); _messages.FileRequested += f => FileRequested?.Invoke(f);
        _input.InstallDrop(this); _input.InstallDrop(_messages);
        _older.LinkClicked += async (_, _) => { if (OlderRequested is not null) await OlderRequested(); };
    }
    public void SelectConversation(string name, bool online, bool group = false)
    {
        _title.Text = name; _subtitle.Text = group ? "Chat nhóm · Tin nhắn được lưu và đồng bộ giữa các máy" : online ? "● Đang trực tuyến · Chat trực tiếp trong mạng LAN" : "○ Đang offline · Tin nhắn sẽ tự gửi khi kết nối lại";
        _input.Enabled = true;
    }
    public void Bind(Guid id, IReadOnlyList<ChatMessage> messages, Guid user) => _messages.Bind(id, messages, user);
    public void BindDetails(IEnumerable<MessageDetails> details) => _messages.BindDetails(details);
    public void SetContext(ChatMessage message, bool edit) => _input.SetContext(message, edit);
    public void ResetContext() => _input.ResetContext();
    public void ResetDraft() => _input.ResetDraft();
    public void ClearConversation()
    {
        _input.ResetDraft(); _input.Enabled = false; _messages.Bind(Guid.Empty, [], Guid.Empty); _older.Visible = false;
        _title.Text = "Chào mừng đến LanChat"; _subtitle.Text = "Chọn cuộc trò chuyện để tiếp tục.";
    }
    public void SetHistoryAvailable(bool value, bool search = false) { _older.Visible = value; _older.Text = search ? "Quay về tin nhắn mới" : "Xem tin nhắn cũ hơn"; }
    public void FocusMessage(Guid id) => _messages.FocusMessage(id);
#if DEBUG
    internal Task SubmitForVerificationAsync(string text) => _input.SubmitForVerificationAsync(text);
    internal void DropForVerification(string[] files) => _input.DropForVerification(files);
#endif
}
