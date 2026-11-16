using LanChat.Domain;
using LanChat.Application;
using ChatMessage = LanChat.Domain.Message;

namespace LanChat.WinForms.Controls;

public sealed class MessageBubbleControl : UserControl
{
    private readonly Label _body = Theme.Label("");
    private readonly Label _state = Theme.Label("", 8);
    private readonly FlowLayoutPanel _files = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
    private readonly FlowLayoutPanel _pictures = new() { Dock = DockStyle.Fill, WrapContents = true };
    private ChatImageService? _images;
    private bool _updatingHeight;
    private readonly ContextMenuStrip _menu = new();
    private readonly ContextMenuStrip _reactionMenu = new() { ShowImageMargin = false };
    private ChatMessage? _message;
    private readonly Button _more = Theme.Secondary("…");
    private readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
    private bool _own;
    private MessageDetails? _details;
    public event Action<ChatMessage, string>? ActionRequested;
    public event Action<AttachmentDto>? FileRequested;
    public MessageBubbleControl()
    {
        Size = new Size(400, 120); Padding = new Padding(12, 8, 12, 8);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        _body.AutoSize = false; _state.AutoSize = false;
        layout.Controls.Add(_body, 0, 0); layout.Controls.Add(_pictures, 0, 1); layout.Controls.Add(_files, 0, 2); layout.Controls.Add(_state, 0, 3); layout.Controls.Add(_actions, 0, 4); Controls.Add(layout);
        foreach (var (caption, action) in new[] { ("Trả lời", "reply"), ("Sao chép", "copy"), ("Chuyển tiếp", "forward"), ("Sửa", "edit"), ("Thu hồi", "recall"), ("Xóa phía tôi", "delete") })
        {
            var item = _menu.Items.Add(caption); item.Tag = action;
            item.Click += (_, _) => { if (_message is not null) { if (action == "copy") { if (_message.Text.Length > 0) Clipboard.SetText(_message.Text); } else ActionRequested?.Invoke(_message, action); } };
        }
        var reactions = new ToolStripMenuItem("Cảm xúc"); reactions.Tag = "react";
        void AddReaction(string caption, string action)
        {
            void SelectReaction(object? sender, EventArgs e)
            {
                if (_message is { IsRecalled: false, Kind: not MessageKind.System } message)
                    ActionRequested?.Invoke(message, action);
            }
            reactions.DropDownItems.Add(caption, null, SelectReaction);
            _reactionMenu.Items.Add(caption, null, SelectReaction);
        }
        foreach (var emoji in new[] { "👍", "❤️", "😂", "😮", "😢" }) AddReaction(emoji, "react:" + emoji);
        AddReaction("Gỡ cảm xúc của tôi", "unreact");
        _menu.Items.Add(reactions);
        var reply = Theme.Secondary("Trả lời"); var react = Theme.Secondary("Cảm xúc"); var more = _more; more.AccessibleName = "Thao tác khác";
        foreach (var button in new[] { reply, react, more }) { button.MinimumSize = new Size(0, 27); button.AutoSize = false; button.Size = new Size(button == more ? 36 : 88, 27); button.Padding = Padding.Empty; button.Font = Theme.SmallFont; button.Margin = new Padding(0, 0, 4, 0); _actions.Controls.Add(button); }
        reply.Click += (_, _) => { if (_message is { IsRecalled: false } m) ActionRequested?.Invoke(m, "reply"); };
        react.Click += (_, _) =>
        {
            var below = react.PointToScreen(new Point(0, react.Height));
            var workingArea = Screen.FromControl(react).WorkingArea;
            var size = _reactionMenu.GetPreferredSize(Size.Empty);
            var x = Math.Max(workingArea.Left, Math.Min(below.X, workingArea.Right - size.Width));
            var y = below.Y + size.Height <= workingArea.Bottom ? below.Y : below.Y - react.Height - size.Height;
            var anchor = react.PointToClient(new Point(x, Math.Max(workingArea.Top, y)));
            _reactionMenu.Show(react, anchor);
        };
        more.Click += (_, _) => _menu.Show(more, new Point(0, more.Height));
        Disposed += (_, _) => { _menu.Dispose(); _reactionMenu.Dispose(); };
        _menu.Opening += (_, _) => { foreach (ToolStripItem item in _menu.Items) item.Enabled = _message is not null && _message.Kind != MessageKind.System && ((string?)item.Tag == "delete" || (!_message.IsRecalled && ((string?)item.Tag is not ("edit" or "recall") || _own))); };
        ContextMenuStrip = _body.ContextMenuStrip = _state.ContextMenuStrip = _menu;
        Resize += (_, _) => UpdateHeight();
        _pictures.ClientSizeChanged += (_, _) => UpdateHeight();
    }
    public void ConfigureImages(ChatImageService images) => _images = images;
    private void UpdateHeight()
    {
        if (_updatingHeight || Controls.Count == 0) return;
        _updatingHeight = true;
        try
        {
        var available = Math.Max(120, _pictures.ClientSize.Width > 0 ? _pictures.ClientSize.Width : Width - Padding.Horizontal - 30);
        foreach (Control picture in _pictures.Controls) picture.Width = Math.Min(180, available - 8);
        var columns = Math.Max(1, available / 188);
        var picturesHeight = (_pictures.Controls.Count + columns - 1) / columns * 173;
        var filesHeight = _files.Controls.Count > 0 ? 48 : 0;
        ((TableLayoutPanel)Controls[0]).RowStyles[1].Height = picturesHeight;
        ((TableLayoutPanel)Controls[0]).RowStyles[2].Height = filesHeight;
        var measured = TextRenderer.MeasureText(_body.Text, _body.Font, new Size(Math.Max(100, Width - Padding.Horizontal - 40), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
        var height = measured.Height + Padding.Vertical + 48 + (_actions.Visible ? 34 : 0) + filesHeight + picturesHeight;
        if (Height != height) Height = height;
        }
        finally { _updatingHeight = false; }
    }
    public void Bind(ChatMessage m, bool own)
    {
        _message = m; _own = own;
        _actions.Visible = m.Kind != MessageKind.System;
        ((TableLayoutPanel)Controls[0]).RowStyles[4].Height = _actions.Visible ? 34 : 0;
        foreach (var button in _actions.Controls.OfType<Button>()) button.Enabled = !m.IsRecalled || button == _more;
        _body.Text = (!own && m.RecipientUserId == Guid.Empty && _details?.SenderName is { } sender ? sender + "\n" : "") + (_details?.ReplyPreview is { } reply ? "↪ " + reply.NormalizeLines(" ") + "\n" : "") + (m.ForwardedFromMessageId.HasValue ? "↗ Đã chuyển tiếp\n" : "") + m.Text;
        _state.Text = new DateTime(m.CreatedAtTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm") +
            (m.IsEdited ? " · Đã sửa" : "") + (own ? _details?.ReadCount > 0 ? " · Đã đọc" : m.Status switch
            { MessageStatus.Delivered => " · Đã nhận", MessageStatus.Synced => " · Đã đồng bộ", _ => " · Đã lưu trên máy" } : "") +
            (_details is null ? "" : "  " + string.Join("  ", _details.Reactions));
        _state.ForeColor = Theme.Muted;
        Controls[0].BackColor = m.Kind == MessageKind.System ? Theme.Canvas : own ? Color.FromArgb(221, 236, 255) : Color.White;
        _body.TextAlign = ContentAlignment.MiddleLeft;
        _state.TextAlign = ContentAlignment.MiddleLeft;
        _actions.FlowDirection = own ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var actionIndex = own ? 0 : 2;
        if (_actions.Controls.GetChildIndex(_more) != actionIndex) _actions.Controls.SetChildIndex(_more, actionIndex);
        Padding = own ? new Padding(Math.Min(130, Width / 5), 5, 14, 5) : new Padding(14, 5, Math.Min(130, Width / 5), 5);
        UpdateHeight();
    }
    public void SetOwnership(bool own) => _own = own;
    public void BindDetails(MessageDetails details)
    {
        var previous = _details;
        if (previous is not null && SamePresentation(previous, details)) return;
        _details = details;
        var files = details.Message.IsRecalled ? [] : details.Attachments;
        if (previous is null || !previous.Attachments.SequenceEqual(files) || previous.Message.IsRecalled != details.Message.IsRecalled)
        {
            foreach (Control c in _files.Controls.Cast<Control>().ToArray()) c.Dispose();
            foreach (Control c in _pictures.Controls.Cast<Control>().ToArray()) c.Dispose();
            foreach (var file in files)
            {
                if (_images is not null && ChatImageService.IsImage(file))
                {
                    var picture = new ImageThumbnailControl(file, _images); picture.Selected += f => FileRequested?.Invoke(f); _pictures.Controls.Add(picture); continue;
                }
                var label = file.MimeType == FolderAttachment.MimeType
                    ? $"Thư mục · {file.FileName}"
                    : $"▤ {file.FileName} ({file.FileSize / 1024d:N1} KB)";
                var link = new LinkLabel { Text = label, AutoSize = true, Margin = new Padding(4, 10, 12, 4) };
                link.LinkClicked += (_, _) => FileRequested?.Invoke(file); _files.Controls.Add(link);
            }
        }
        _files.Visible = _files.Controls.Count > 0; _pictures.Visible = _pictures.Controls.Count > 0;
        Bind(details.Message, _own);
        EnsureImagesLoaded();
    }
    private static bool SamePresentation(MessageDetails old, MessageDetails next) =>
        old.Message.Text == next.Message.Text && old.Message.Status == next.Message.Status && old.Message.IsEdited == next.Message.IsEdited &&
        old.Message.IsRecalled == next.Message.IsRecalled && old.ReplyPreview == next.ReplyPreview && old.SenderName == next.SenderName &&
        old.ReceivedCount == next.ReceivedCount && old.ReadCount == next.ReadCount && old.Attachments.SequenceEqual(next.Attachments) && old.Reactions.SequenceEqual(next.Reactions);
    public void EnsureImagesLoaded() { foreach (var picture in _pictures.Controls.OfType<ImageThumbnailControl>()) picture.EnsureLoaded(); }
}

public sealed class MessageListControl : UserControl
{
    private readonly FlowLayoutPanel _flow = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Canvas };
    private readonly Dictionary<Guid, MessageBubbleControl> _bubbles = [];
    private Guid? _conversation;
    private bool _resizing;
    private bool _followEnd;
    private (Guid Id, int Top)? _scrollAnchor;
    private Guid? _lastMessage;
    private Guid? _highlight;
    public event Action<bool>? NewMessages;
    public event Action? ReachedBottom;
    private ChatImageService? _images;
    public event Action<ChatMessage, string>? ActionRequested;
    public event Action<AttachmentDto>? FileRequested;
    public MessageListControl()
    {
        Dock = DockStyle.Fill; Controls.Add(_flow);
        _flow.ClientSizeChanged += (_, _) => ResizeItems();
        _flow.Scroll += (_, _) => { foreach (var bubble in _bubbles.Values) bubble.EnsureImagesLoaded(); if (AtBottom) { NewMessages?.Invoke(false); ReachedBottom?.Invoke(); } };
    }
    public void ConfigureImages(ChatImageService images) { _images = images; foreach (var bubble in _bubbles.Values) bubble.ConfigureImages(images); }
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
    public void Bind(Guid conversation, IReadOnlyList<ChatMessage> messages, Guid localUser, bool hasDetails = false)
    {
        var changedConversation = _conversation != conversation;
        var nearBottom = AtBottom;
        var anchor = _flow.Controls.OfType<MessageBubbleControl>().FirstOrDefault(c => c.Bottom > 0);
        _scrollAnchor = anchor is null ? null : (_bubbles.First(x => x.Value == anchor).Key, anchor.Top);
        _followEnd = changedConversation || nearBottom;
        var last = messages.Count == 0 ? (Guid?)null : messages[messages.Count - 1].Id;
        if (_followEnd) NewMessages?.Invoke(false);
        else if (_lastMessage != last) NewMessages?.Invoke(true);
        _lastMessage = last;
        if (changedConversation) { foreach (Control c in _flow.Controls.Cast<Control>().ToArray()) c.Dispose(); _bubbles.Clear(); _conversation = conversation; }
        _flow.SuspendLayout();
        foreach (var id in _bubbles.Keys.Except(messages.Select(x => x.Id)).ToArray()) { _bubbles[id].Dispose(); _bubbles.Remove(id); }
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (!_bubbles.TryGetValue(m.Id, out var bubble))
            {
                bubble = new MessageBubbleControl(); bubble.ActionRequested += (m, a) => ActionRequested?.Invoke(m, a); bubble.FileRequested += f => FileRequested?.Invoke(f);
                if (_images is not null) bubble.ConfigureImages(_images);
                _bubbles.Add(m.Id, bubble); _flow.Controls.Add(bubble);
            }
            if (!hasDetails) bubble.Bind(m, m.SenderUserId == localUser);
            else bubble.SetOwnership(m.SenderUserId == localUser);
            if (_flow.Controls.GetChildIndex(bubble) != i) _flow.Controls.SetChildIndex(bubble, i);
        }
        ResizeItems(); _flow.ResumeLayout(true);
        if (!hasDetails) RestoreScroll();
    }
    public void BindDetails(IEnumerable<MessageDetails> details)
    {
        _flow.SuspendLayout();
        foreach (var d in details)
            if (_bubbles.TryGetValue(d.Message.Id, out var bubble)) bubble.BindDetails(d);
        _flow.ResumeLayout(true);
        RestoreScroll();
    }

    public bool AtBottom => !_flow.VerticalScroll.Visible ||
        _flow.VerticalScroll.Value + _flow.VerticalScroll.LargeChange >= _flow.VerticalScroll.Maximum - 24;

    private void RestoreScroll()
    {
        if (_followEnd) ScrollToEnd();
        else if (_scrollAnchor is { } anchor && _bubbles.TryGetValue(anchor.Id, out var control))
            _flow.AutoScrollPosition = new Point(0, Math.Max(0, -_flow.AutoScrollPosition.Y + control.Top - anchor.Top));
    }

    public void ScrollToEnd()
    {
        if (_flow.Controls.Count > 0)
            _flow.AutoScrollPosition = new Point(0, Math.Max(0, _flow.VerticalScroll.Maximum));
        NewMessages?.Invoke(false);
    }
    public void FocusMessage(Guid id)
    {
        if (!_bubbles.TryGetValue(id, out var bubble)) return;
        if (_highlight is Guid last && _bubbles.TryGetValue(last, out var old)) old.BorderStyle = BorderStyle.None;
        _flow.ScrollControlIntoView(bubble); bubble.BorderStyle = BorderStyle.FixedSingle; _highlight = id;
    }
}

public sealed record ComposerSubmission(string Text, string[] Files, Guid? ReplyTo, Guid? EditId);

internal sealed record ComposerDraft(string Text, string[] Files, Guid? Reply, Guid? Edit, string Context);

public sealed class ChatInputControl : UserControl
{
    private readonly PromptTextBox _text = new() { Multiline = true, Dock = DockStyle.Fill, MaxLength = 16000, ScrollBars = ScrollBars.Vertical,
        PlaceholderText = "Nhập tin nhắn…  Enter để gửi · Shift+Enter để xuống dòng" };
    private readonly Button _send = Theme.Button("Gửi ➤");
    private readonly FlowLayoutPanel _previews = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
    private readonly LinkLabel _context = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly List<string> _paths = [];
    private Guid? _replyTo, _editId;
    private int _draftGeneration;
    private bool _sending;
    public event Func<ComposerSubmission, Task>? SendRequested;
    public event Action<int>? HeightRequested;
    public ChatInputControl()
    {
        Dock = DockStyle.Fill; Padding = new Padding(12); BackColor = Color.White;
        var root = Theme.Stack(3); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 119)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 99));
        var attach = Theme.Secondary("+ Đính kèm"); attach.AutoSize = false; attach.Size = new Size(106, 38); attach.Padding = Padding.Empty;
        attach.Anchor = AnchorStyles.Top | AnchorStyles.Left; attach.AccessibleName = "Chọn tệp hoặc thư mục"; attach.MinimumSize = Size.Empty; layout.Controls.Add(attach, 0, 0);
        layout.Controls.Add(_text, 1, 0); layout.Controls.Add(_send, 2, 0);
        _send.AutoSize = false; _send.Size = new Size(88, 38); _send.MinimumSize = Size.Empty;
        _send.Padding = Padding.Empty; _send.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        root.Controls.Add(_context, 0, 0); root.Controls.Add(_previews, 0, 1); root.Controls.Add(layout, 0, 2); Controls.Add(root);
        var attachments = new ContextMenuStrip { ShowImageMargin = false };
        attachments.Items.Add("Thêm tệp…", null, (_, _) =>
        {
            using var dialog = new OpenFileDialog { Multiselect = true, Title = "Chọn tệp đính kèm", Filter = "Tất cả tệp|*.*" };
            if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles(dialog.FileNames);
        });
        attachments.Items.Add("Thêm thư mục…", null, (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Chọn thư mục đính kèm", ShowNewFolderButton = false };
            if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles([dialog.SelectedPath]);
        });
        attach.Click += (_, _) => { if (!_sending && !_editId.HasValue) attachments.Show(attach, new Point(0, attach.Height)); };
        Disposed += (_, _) => attachments.Dispose();
        _context.LinkClicked += (_, _) => { if (!_sending) ResetContext(); };
        InstallDrop(this); InstallDrop(_text); InstallDrop(_previews);
        _send.Click += async (_, _) => await SendAsync();
        _text.Margin = new Padding(3, 4, 3, 5);
        _text.AccessibleName = "Nhập tin nhắn";
        _text.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift && !_text.IsComposing) { e.SuppressKeyPress = true; await SendAsync(); } };
    }
    private async Task SendAsync()
    {
        if (_sending || !_send.Enabled || (string.IsNullOrWhiteSpace(_text.Text) && _paths.Count == 0) || SendRequested is null) return;
        var generation = _draftGeneration;
        _sending = true; _send.Enabled = false; _text.ReadOnly = true;
        try
        {
            await SendRequested(new(_text.Text, _paths.ToArray(), _replyTo, _editId));
            if (!IsDisposed && generation == _draftGeneration) ResetDraft();
        }
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, ex.Message, "Chưa gửi được", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _sending = false; if (!IsDisposed) { _send.Enabled = true; _text.ReadOnly = false; } }
    }
    public void InstallDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) => e.Effect = Enabled && !_sending && !_editId.HasValue && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        control.DragDrop += (_, e) => { if (Enabled && !_sending && e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths); };
    }
    public void AddFiles(IEnumerable<string> paths)
    {
        if (_sending || _editId.HasValue) return;
        foreach (var path in paths.Where(path => File.Exists(path) || Directory.Exists(path)))
            if (!_paths.Contains(path, StringComparer.OrdinalIgnoreCase) && _paths.Count < 20) _paths.Add(path);
        RefreshPreviews();
    }
    private void RefreshPreviews()
    {
        foreach (Control c in _previews.Controls.Cast<Control>().ToArray()) c.Dispose();
        foreach (var path in _paths.ToArray()) { var preview = new AttachmentPreviewControl(path); preview.RemoveRequested += () => { if (!_sending) { _paths.Remove(path); RefreshPreviews(); } }; _previews.Controls.Add(preview); }
        UpdateLayoutHeight();
    }
    private void UpdateLayoutHeight()
    {
        var root = (TableLayoutPanel)Controls[0]; root.RowStyles[0].Height = _context.Text.Length > 0 ? 26 : 0; root.RowStyles[1].Height = _paths.Count > 0 ? 82 : 0;
        HeightRequested?.Invoke(92 + (int)root.RowStyles[0].Height + (int)root.RowStyles[1].Height);
    }
    public void SetContext(ChatMessage message, bool edit)
    {
        if (_sending) return;
        _replyTo = edit ? null : message.Id; _editId = edit ? message.Id : null;
        _context.Text = (edit ? "Sửa: " : "Trả lời: ") + message.Text.NormalizeLines(" ") + "  · Bấm để hủy";
        if (edit) { _paths.Clear(); RefreshPreviews(); _text.Text = message.Text; }
        UpdateLayoutHeight(); _text.Focus();
    }
    public void ResetContext() { if (_editId.HasValue) _text.Clear(); _replyTo = _editId = null; _context.Text = ""; UpdateLayoutHeight(); }
    public void ResetDraft() => RestoreDraft(null);
    internal ComposerDraft Draft() => new(_text.Text, _paths.ToArray(), _replyTo, _editId, _context.Text);
    internal void RestoreDraft(ComposerDraft? draft)
    {
        _draftGeneration++;
        _text.Text = draft?.Text ?? ""; _paths.Clear();
        if (draft is not null) _paths.AddRange(draft.Files);
        _replyTo = draft?.Reply; _editId = draft?.Edit; _context.Text = draft?.Context ?? "";
        RefreshPreviews();
    }
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
    private readonly Button _find = Theme.Secondary("Tìm tin");
    private readonly Button _info = Theme.Secondary("Thông tin");
    private readonly LinkLabel _new = new() { Text = "Tin mới ↓", AutoSize = true, Visible = false, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, BackColor = Color.White };
    public event Action<bool>? PanelRequested;
    public event Action? ReadRequested;
    private readonly LinkLabel _older = new() { Text = "Xem tin nhắn cũ hơn", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    public event Func<ComposerSubmission, Task>? SendRequested;
    public event Func<Task>? OlderRequested;
    public event Action<ChatMessage, string>? ActionRequested;
    public event Action<AttachmentDto>? FileRequested;
    public ChatViewControl()
    {
        Dock = DockStyle.Fill;
        var layout = Theme.Stack(5);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_title, 0, 0); header.Controls.Add(_find, 1, 0); header.Controls.Add(_info, 2, 0);
        _find.Click += (_, _) => PanelRequested?.Invoke(true); _info.Click += (_, _) => PanelRequested?.Invoke(false);
        _find.Enabled = _info.Enabled = false;
        var history = new Panel { Dock = DockStyle.Fill };
        history.Controls.Add(_messages); history.Controls.Add(_new); _new.BringToFront();
        history.Resize += (_, _) => _new.Location = new Point(Math.Max(0, history.Width - _new.Width - 24), Math.Max(0, history.Height - _new.Height - 12));
        _messages.NewMessages += value => _new.Visible = value || _searchWindow;
        _messages.ReachedBottom += () => ReadRequested?.Invoke();
        _new.LinkClicked += async (_, _) => { if (_searchWindow && OlderRequested is not null) await OlderRequested(); else { _messages.ScrollToEnd(); ReadRequested?.Invoke(); } };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        _title.Padding = new Padding(14, 8, 0, 0); _subtitle.Padding = new Padding(14, 0, 0, 0); _subtitle.ForeColor = Theme.Muted;
        _title.AutoSize = false; _title.AutoEllipsis = true; _subtitle.AutoSize = false; _subtitle.AutoEllipsis = true;
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(_subtitle, 0, 1); layout.Controls.Add(_older, 0, 2); layout.Controls.Add(history, 0, 3); layout.Controls.Add(_input, 0, 4);
        Controls.Add(layout); _input.SendRequested += async text => { if (SendRequested is not null) await SendRequested(text); };
        _input.HeightRequested += height => layout.RowStyles[4].Height = height;
        _messages.ActionRequested += (m, a) => ActionRequested?.Invoke(m, a); _messages.FileRequested += f => FileRequested?.Invoke(f);
        _input.InstallDrop(this); _input.InstallDrop(_messages);
        _older.LinkClicked += async (_, _) => { if (OlderRequested is not null) await OlderRequested(); };
    }
    public void SelectConversation(string name, bool online, bool group = false)
    {
        _title.Text = name; _subtitle.Text = group ? "Chat nhóm · Tin nhắn được lưu và đồng bộ giữa các máy" : online ? "● Đang trực tuyến · Chat trực tiếp trong mạng LAN" : "○ Đang offline · Tin nhắn sẽ tự gửi khi kết nối lại";
        _input.Enabled = true; _find.Enabled = _info.Enabled = true;
    }
    internal void Bind(Guid id, IReadOnlyList<ChatMessage> messages, Guid user, MessageDetails[] details)
    { _messages.Bind(id, messages, user, true); _messages.BindDetails(details); }
    internal ComposerDraft Draft() => _input.Draft();
    internal void RestoreDraft(ComposerDraft? draft) => _input.RestoreDraft(draft);
    internal void ScrollToEnd() => _messages.ScrollToEnd();
    internal bool AtBottom => _messages.AtBottom;
    public void ConfigureImages(ChatImageService images) => _messages.ConfigureImages(images);
    public void BindDetails(IEnumerable<MessageDetails> details) => _messages.BindDetails(details);
    public void SetContext(ChatMessage message, bool edit) => _input.SetContext(message, edit);
    public void ResetContext() => _input.ResetContext();
    public void ResetDraft() => _input.ResetDraft();
    public void ClearConversation()
    {
        _input.ResetDraft(); _input.Enabled = false; _find.Enabled = _info.Enabled = false; _messages.Bind(Guid.Empty, [], Guid.Empty);
        _searchWindow = false; _older.Visible = _new.Visible = false;
        _title.Text = "Chào mừng đến LanChat"; _subtitle.Text = "Chọn cuộc trò chuyện để tiếp tục.";
    }
    private bool _searchWindow;
    public void SetHistoryAvailable(bool value, bool search = false) { _searchWindow = search; _older.Visible = value; _older.Text = search ? "Quay về tin nhắn mới" : "Xem tin nhắn cũ hơn"; if (search) _new.Visible = true; }
    public void FocusMessage(Guid id) => _messages.FocusMessage(id);
#if DEBUG
    internal Task SubmitForVerificationAsync(string text) => _input.SubmitForVerificationAsync(text);
    internal void DropForVerification(string[] files) => _input.DropForVerification(files);
#endif
}
