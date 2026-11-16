using System.Drawing.Drawing2D;
using System.Globalization;
using LanChat.Domain;
using LanChat.Application;
using ChatMessage = LanChat.Domain.Message;

namespace LanChat.WinForms.Controls;

public sealed class MessageBubbleControl : UserControl
{
    private static readonly (Color Background, Color Ink)[] SenderColors =
    [
        (Color.FromArgb(221, 233, 255), Color.FromArgb(37, 78, 161)),
        (Color.FromArgb(218, 239, 231), Color.FromArgb(26, 100, 77)),
        (Color.FromArgb(242, 229, 252), Color.FromArgb(107, 56, 137)),
        (Color.FromArgb(251, 232, 219), Color.FromArgb(144, 69, 35)),
        (Color.FromArgb(252, 226, 235), Color.FromArgb(143, 56, 88))
    ];
    private static readonly Color SentBackground = Color.FromArgb(228, 239, 255);
    private static readonly Color SentBorder = Color.FromArgb(201, 219, 245);
    private static readonly Color SystemBackground = Color.FromArgb(235, 240, 247);
    private readonly TableLayoutPanel _card = new() { ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
    private readonly PersonNameLabel _sender = Theme.NameLabel("", 9, true);
    private readonly Label _body = Theme.Label("");
    private readonly Label _state = Theme.Label("", 8);
    private readonly FlowLayoutPanel _files = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, Margin = Padding.Empty };
    private readonly FlowLayoutPanel _pictures = new() { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
    private ChatImageService? _images;
    private bool _updatingHeight;
    private readonly ContextMenuStrip _menu = new();
    private readonly ContextMenuStrip _reactionMenu = new() { ShowImageMargin = false };
    private ChatMessage? _message;
    private readonly Button _reply = Theme.Secondary("❝");
    private readonly Button _react = Theme.Secondary("♥");
    private readonly Button _more = Theme.Secondary("…");
    private readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
    private bool _own, _startsGroup, _unread;
    private AttachmentDto[] _attachments = [];
    private Rectangle _bubbleBounds, _avatarBounds;
    private Color _border;
    private (Color Background, Color Ink) _avatarColor;
    private string _initials = "";
    internal Guid MessageId => _message?.Id ?? Guid.Empty;
    internal bool Highlighted { get => _highlighted; set { _highlighted = value; Invalidate(); } }
    private bool _highlighted;
    public event Action<ChatMessage, string>? ActionRequested;
    public event Action<AttachmentDto>? FileRequested;
    public MessageBubbleControl()
    {
        Size = new Size(400, 120); Margin = Padding.Empty;
        BackColor = Theme.Canvas; DoubleBuffered = true; ResizeRedraw = true;
        _card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var control in new Control[] { _sender, _body, _pictures, _files, _state, _actions })
        {
            _card.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            _card.Controls.Add(control, 0, _card.RowStyles.Count - 1);
        }
        foreach (var label in new[] { _sender, _body, _state })
        {
            label.AutoSize = false; label.Margin = Padding.Empty; label.UseMnemonic = false;
        }
        _sender.AutoEllipsis = true;
        _state.AutoEllipsis = true;
        Controls.Add(_card);
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
        _reply.AccessibleName = "Trả lời";
        _react.AccessibleName = "Cảm xúc";
        _more.AccessibleName = "Thao tác khác";
        var actionFont = Theme.GetFont(12, true);
        foreach (var button in new[] { _reply, _react, _more })
        {
            button.MinimumSize = new Size(36, 27);
            button.Padding = Padding.Empty;
            button.Font = actionFont;
            button.Margin = new Padding(0, 0, 4, 0);
            _actions.Controls.Add(button);
        }
        _reply.Click += (_, _) => { if (_message is { IsRecalled: false } m) ActionRequested?.Invoke(m, "reply"); };
        _react.Click += (_, _) =>
        {
            var below = _react.PointToScreen(new Point(0, _react.Height));
            var workingArea = Screen.FromControl(_react).WorkingArea;
            var size = _reactionMenu.GetPreferredSize(Size.Empty);
            var x = Math.Max(workingArea.Left, Math.Min(below.X, workingArea.Right - size.Width));
            var y = below.Y + size.Height <= workingArea.Bottom ? below.Y : below.Y - _react.Height - size.Height;
            var anchor = _react.PointToClient(new Point(x, Math.Max(workingArea.Top, y)));
            _reactionMenu.Show(_react, anchor);
        };
        _more.Click += (_, _) => _menu.Show(_more, new Point(0, _more.Height));
        Disposed += (_, _) => { _menu.Dispose(); _reactionMenu.Dispose(); };
        _menu.Opening += (_, _) => { foreach (ToolStripItem item in _menu.Items) item.Enabled = _message is not null && _message.Kind != MessageKind.System && ((string?)item.Tag == "delete" || (!_message.IsRecalled && ((string?)item.Tag is not ("edit" or "recall") || _own))); };
        ContextMenuStrip = _card.ContextMenuStrip = _sender.ContextMenuStrip = _body.ContextMenuStrip = _state.ContextMenuStrip = _menu;
        Resize += (_, _) => UpdateLayout();
        DpiChangedAfterParent += (_, _) => UpdateLayout();
    }
    public void ConfigureImages(ChatImageService images) => _images = images;

    internal void AttachActionToolTips(ToolTip toolTips)
    {
        var buttons = new[] { _reply, _react, _more };
        foreach (var button in buttons) toolTips.SetToolTip(button, button.AccessibleName);
        Disposed += (_, _) =>
        {
            foreach (var button in buttons) toolTips.SetToolTip(button, "");
        };
    }

    private void UpdateLayout()
    {
        if (_updatingHeight || _message is null) return;
        _updatingHeight = true;
        try
        {
            var system = _message.Kind == MessageKind.System;
            var gutter = Math.Min(this.ScalePixels(14), Width / 8);
            var avatarSpace = !system && Width >= this.ScalePixels(130) ? this.ScalePixels(42) : 0;
            var cardWidth = Math.Max(1, Math.Min(Width - gutter * 2 - avatarSpace,
                Math.Min(this.ScalePixels(system ? 500 : 660), Math.Max(this.ScalePixels(340), Width * 4 / 5))));
            var x = system ? (Width - cardWidth) / 2 : _own ? Width - gutter - avatarSpace - cardWidth : gutter + avatarSpace;
            var y = this.ScalePixels(_startsGroup ? 12 : 3);
            var insetX = Math.Min(this.ScalePixels(12), (cardWidth - 1) / 2);
            var insetY = this.ScalePixels(7);
            var contentWidth = Math.Max(1, cardWidth - insetX * 2);

            _card.SuspendLayout();
            var picturesHeight = 0;
            if (_pictures.Controls.Count > 0)
            {
                var picture = _pictures.Controls[0];
                var pictureWidth = Math.Max(1, Math.Min(this.ScalePixels(180), contentWidth - picture.Margin.Horizontal));
                foreach (Control item in _pictures.Controls) item.Width = pictureWidth;
                var columns = Math.Max(1, contentWidth / (pictureWidth + picture.Margin.Horizontal));
                picturesHeight = (_pictures.Controls.Count + columns - 1) / columns * (picture.Height + picture.Margin.Vertical);
            }
            var bodyHeight = _body.Text.Length == 0 ? 0 : TextRenderer.MeasureText(_body.Text, _body.Font,
                new Size(contentWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height;
            var actionsWidth = _actions.Controls.Cast<Button>().Sum(button => button.GetPreferredSize(Size.Empty).Width + button.Margin.Horizontal);
            _reply.Visible = _react.Visible = contentWidth >= actionsWidth;
            var actionsHeight = _actions.Controls.Cast<Button>().Max(button => button.GetPreferredSize(Size.Empty).Height + button.Margin.Vertical);
            var rowHeights = new[] { !system && _startsGroup ? _sender.PreferredHeight + this.ScalePixels(4) : 0, bodyHeight,
                picturesHeight, _files.Controls.Count > 0 ? this.ScalePixels(45) : 0, _state.PreferredHeight + this.ScalePixels(5),
                system ? 0 : actionsHeight };
            for (var i = 0; i < rowHeights.Length; i++) _card.RowStyles[i].Height = rowHeights[i];
            var height = rowHeights.Sum();
            _card.SetBounds(x + insetX, y + insetY, contentWidth, height);
            _card.ResumeLayout(true);
            _bubbleBounds = new Rectangle(x, y, cardWidth, height + insetY * 2);
            _avatarBounds = avatarSpace == 0 || !_startsGroup ? Rectangle.Empty : new Rectangle(_own ? _bubbleBounds.Right + this.ScalePixels(8) : gutter,
                y + this.ScalePixels(2), this.ScalePixels(32), this.ScalePixels(32));
            var rowHeight = Math.Max(_bubbleBounds.Bottom, _avatarBounds.Bottom);
            if (Height != rowHeight) Height = rowHeight;
            Invalidate();
        }
        finally { _updatingHeight = false; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_bubbleBounds.Width <= 1 || _bubbleBounds.Height <= 1) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = _bubbleBounds;
        bounds.Width--; bounds.Height--;
        var diameter = Math.Min(this.ScalePixels(20), Math.Min(bounds.Width, bounds.Height));
        using var shape = new GraphicsPath();
        shape.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        shape.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        shape.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        shape.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        shape.CloseFigure();
        using var fill = new SolidBrush(_card.BackColor);
        using var edge = new Pen(_highlighted || _unread ? Theme.Blue : _border, _highlighted ? 2 : 1);
        e.Graphics.FillPath(fill, shape);
        e.Graphics.DrawPath(edge, shape);

        if (_avatarBounds.IsEmpty) return;
        using var avatar = new SolidBrush(_avatarColor.Background);
        e.Graphics.FillEllipse(avatar, _avatarBounds);
        TextRenderer.DrawText(e.Graphics, _initials, _sender.Font, _avatarBounds, _avatarColor.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    internal void Bind(ChatMessage m, MessageDetails? details, bool own, bool startsGroup, bool unread)
    {
        _message = m; _own = own; _startsGroup = startsGroup; _unread = unread;
        var system = m.Kind == MessageKind.System;
        var name = details?.SenderName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = own ? "Bạn" : "Người dùng";
        _avatarColor = own ? SenderColors[0] : SenderColors[(int)((uint)m.SenderUserId.GetHashCode() % SenderColors.Length)];
        var sender = new PersonName(name!, details?.SenderDepartment);
        _sender.SetName(sender);
        _sender.ForeColor = _avatarColor.Ink;
        _initials = Initials(name!);
        AccessibleName = system ? "Thông báo nhóm" : sender.ToString() + (unread ? " · Chưa đọc" : "");
        _sender.Visible = !system && startsGroup;
        _actions.Visible = !system;
        foreach (var button in _actions.Controls.OfType<Button>()) button.Enabled = !m.IsRecalled || button == _more;
        _body.Text = (details?.ReplyPreview is { } reply ? "↪ " + reply.NormalizeLines(" ") + "\n" : "") +
            (m.ForwardedFromMessageId.HasValue ? "↗ Đã chuyển tiếp\n" : "") + m.Text;
        _state.Text = new DateTime(m.CreatedAtTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm") +
            (m.IsEdited ? " · Đã sửa" : "") + (own && !system ? details?.ReadCount > 0 ? " · Đã đọc" : m.Status switch
            { MessageStatus.Delivered => " · Đã nhận", MessageStatus.Synced => " · Đã đồng bộ", _ => " · Đã lưu trên máy" } : "") +
            (details is null ? "" : "  " + string.Join("  ", details.Reactions));
        _state.ForeColor = Theme.Muted;
        _card.BackColor = system ? SystemBackground : own ? SentBackground : Color.White;
        _border = own && !system ? SentBorder : Theme.Border;
        _body.ForeColor = system ? Theme.Muted : Theme.Ink;
        _body.TextAlign = system ? ContentAlignment.MiddleCenter : ContentAlignment.TopLeft;
        _state.TextAlign = system ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
        _actions.FlowDirection = own ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var actionIndex = own ? 0 : 2;
        if (_actions.Controls.GetChildIndex(_more) != actionIndex) _actions.Controls.SetChildIndex(_more, actionIndex);
        BindAttachments(m.IsRecalled ? [] : details?.Attachments ?? []);
        UpdateLayout();
    }

    private static string Initials(string name)
    {
        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var first = StringInfo.GetNextTextElement(words[0]);
        return (words.Length == 1 ? first : first + StringInfo.GetNextTextElement(words[words.Length - 1])).ToUpperInvariant();
    }

    private void BindAttachments(AttachmentDto[] files)
    {
        if (_attachments.SequenceEqual(files)) return;
        _attachments = files;
        foreach (Control c in _files.Controls.Cast<Control>().ToArray()) c.Dispose();
        foreach (Control c in _pictures.Controls.Cast<Control>().ToArray()) c.Dispose();
        foreach (var file in files)
        {
            if (_images is not null && ChatImageService.IsImage(file))
            {
                var picture = new ImageThumbnailControl(file, _images);
                picture.Selected += f => FileRequested?.Invoke(f);
                Theme.AddAtCurrentDpi(_pictures, picture);
                continue;
            }
            var label = file.MimeType == FolderAttachment.MimeType
                ? $"Thư mục · {file.FileName}"
                : $"▤ {file.FileName} ({file.FileSize / 1024d:N1} KB)";
            var link = new LinkLabel { Text = label, AutoSize = true, Margin = new Padding(4, 10, 12, 4) };
            link.LinkClicked += (_, _) => FileRequested?.Invoke(file);
            Theme.AddAtCurrentDpi(_files, link);
        }
    }
    public void EnsureImagesLoaded() { foreach (var picture in _pictures.Controls.OfType<ImageThumbnailControl>()) picture.EnsureLoaded(); }

#if DEBUG
    internal void ClickActionsForVerification(string emoji)
    {
        if (_reply.Text != "❝" || _react.Text != "♥" || !_reply.Visible || !_react.Visible)
            throw new InvalidOperationException("Thiếu nút biểu tượng Trả lời hoặc Cảm xúc.");
        _react.PerformClick();
        if (!_reactionMenu.Visible) throw new InvalidOperationException("Nút trái tim chưa mở danh sách cảm xúc.");
        _reactionMenu.Items.Cast<ToolStripItem>().Single(item => item.Text == emoji).PerformClick();
        _reactionMenu.Close();
        _reply.PerformClick();
    }
#endif
}

public sealed class MessageListControl : UserControl
{
    private readonly FlowLayoutPanel _flow = new BufferedFlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, BackColor = Theme.Canvas, Padding = new Padding(0, 0, 0, 10) };
    private readonly Label _unreadDivider = Theme.Label("Tin nhắn chưa đọc", 9, true);
    private readonly ToolTip _actionToolTips = new();
    private readonly Dictionary<Guid, MessageBubbleControl> _bubbles = [];
    private Guid? _conversation;
    private bool _resizing, _resizeQueued, _binding;
    private Guid? _firstNewMessage;
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
        _unreadDivider.Dock = DockStyle.None;
        _unreadDivider.AutoSize = false;
        _unreadDivider.TextAlign = ContentAlignment.MiddleCenter;
        _unreadDivider.Margin = Padding.Empty;
        _unreadDivider.BackColor = Theme.Canvas;
        _unreadDivider.Paint += (_, e) =>
        {
            var halfText = TextRenderer.MeasureText(_unreadDivider.Text, _unreadDivider.Font).Width / 2 + this.ScalePixels(12);
            var x = _unreadDivider.Width / 2; var y = _unreadDivider.Height / 2;
            var inset = this.ScalePixels(14);
            if (x - halfText <= inset) return;
            using var line = new Pen(_unreadDivider.ForeColor);
            e.Graphics.DrawLine(line, inset, y, x - halfText, y);
            e.Graphics.DrawLine(line, x + halfText, y, _unreadDivider.Width - inset, y);
        };
        Disposed += (_, _) =>
        {
            _unreadDivider.Dispose();
            _actionToolTips.Dispose();
        };
        _flow.HandleCreated += (_, _) => QueueResize();
        _flow.SizeChanged += (_, _) => QueueResize();
        DpiChangedAfterParent += (_, _) => QueueResize();
        _flow.Scroll += (_, _) => OnScrolled();
        _flow.MouseWheel += (_, _) => OnScrolled();
    }
    private void OnScrolled()
    {
        if (_binding || _resizing) return;
        foreach (var bubble in _bubbles.Values) bubble.EnsureImagesLoaded();
        if (AtBottom) { NewMessages?.Invoke(false); ReachedBottom?.Invoke(); }
    }
    public void ConfigureImages(ChatImageService images) { _images = images; foreach (var bubble in _bubbles.Values) bubble.ConfigureImages(images); }

    private void QueueResize()
    {
        if (_resizeQueued || !_flow.IsHandleCreated || _flow.IsDisposed) return;
        // Reflow after WinForms has finished calculating the viewport and its scrollbars.
        _resizeQueued = true;
        _flow.BeginInvoke((Action)(() =>
        {
            _resizeQueued = false;
            if (!_flow.IsDisposed) ResizeItems();
        }));
    }

    private void ResizeItems()
    {
        if (_resizing || _flow.ClientSize.Width <= 0) return;
        _resizing = true;
        try
        {
            var followEnd = !_binding && AtBottom;
            var anchor = !_binding && !followEnd ? _flow.Controls.OfType<MessageBubbleControl>().FirstOrDefault(c => c.Bottom > 0) : null;
            var anchorTop = anchor?.Top ?? 0;
            var scrollBar = SystemInformation.GetVerticalScrollBarWidthForDpi(this.LayoutDpi());
            var viewport = Math.Min(_flow.ClientSize.Width, _flow.Width - scrollBar);
            var width = Math.Max(1, viewport - _flow.Padding.Horizontal - this.ScalePixels(2));
            _flow.SuspendLayout();
            try
            {
                foreach (var bubble in _bubbles.Values) bubble.Width = width;
                _unreadDivider.Size = new Size(width, _unreadDivider.PreferredHeight + this.ScalePixels(14));
            }
            finally { _flow.ResumeLayout(true); }
            if (followEnd) ScrollToEnd();
            else if (anchor is not null) ScrollTo(anchor.Top - anchorTop);
        }
        finally { _resizing = false; }
    }
    internal void Bind(Guid conversation, IReadOnlyList<ChatMessage> messages, Guid localUser, MessageDetails[] details, bool search = false)
    {
        var changedConversation = _conversation != conversation;
        var followEnd = changedConversation || AtBottom;
        var firstVisible = _flow.Controls.OfType<MessageBubbleControl>().FirstOrDefault(c => c.Bottom > 0);
        var anchorId = firstVisible?.MessageId;
        var anchorTop = firstVisible?.Top ?? 0;
        var last = messages.Count == 0 ? (Guid?)null : messages[messages.Count - 1].Id;
        var byId = new Dictionary<Guid, MessageDetails>(details.Length);
        foreach (var detail in details) byId[detail.Message.Id] = detail;
        var ids = new HashSet<Guid>(messages.Select(x => x.Id));
        _binding = true;
        _flow.SuspendLayout();
        try
        {
            if (changedConversation)
            {
                _conversation = conversation;
                _firstNewMessage = _highlight = null;
            }
            var firstUnread = search ? null : messages.FirstOrDefault(m => IsUnread(m, localUser));
            if (firstUnread is not null) _firstNewMessage = firstUnread.Id;
            var markerId = !search && _firstNewMessage is Guid id && ids.Contains(id) ? _firstNewMessage : null;
            _unreadDivider.Text = firstUnread is null ? "Tin nhắn mới" : "Tin nhắn chưa đọc";
            _unreadDivider.ForeColor = firstUnread is null ? Theme.Muted : Theme.Blue;
            _unreadDivider.Visible = markerId.HasValue;
            if (markerId.HasValue && _unreadDivider.Parent is null) _flow.Controls.Add(_unreadDivider);
            foreach (var removed in _bubbles.Keys.Where(id => !ids.Contains(id)).ToArray())
            { _bubbles[removed].Dispose(); _bubbles.Remove(removed); }

            ChatMessage? previous = null;
            var index = 0;
            foreach (var m in messages)
            {
                var hasDivider = m.Id == markerId;
                if (hasDivider) SetPosition(_unreadDivider, index++);
                if (!_bubbles.TryGetValue(m.Id, out var bubble))
                {
                    bubble = new MessageBubbleControl(); bubble.ActionRequested += (message, action) => ActionRequested?.Invoke(message, action);
                    bubble.FileRequested += file => FileRequested?.Invoke(file);
                    if (_images is not null) bubble.ConfigureImages(_images);
                    _bubbles.Add(m.Id, bubble);
                    Theme.AddAtCurrentDpi(_flow, bubble);
                    bubble.AttachActionToolTips(_actionToolTips);
                }
                byId.TryGetValue(m.Id, out var detail);
                var startsGroup = hasDivider || previous is null || m.Kind == MessageKind.System || previous.Kind == MessageKind.System ||
                    previous.SenderUserId != m.SenderUserId || Math.Abs(m.CreatedAtTicks - previous.CreatedAtTicks) >= TimeSpan.FromMinutes(5).Ticks;
                bubble.Bind(m, detail, m.SenderUserId == localUser, startsGroup, IsUnread(m, localUser));
                bubble.Highlighted = m.Id == _highlight;
                SetPosition(bubble, index++);
                previous = m;
            }
            ResizeItems();
            _flow.ResumeLayout(true);

            if (changedConversation && firstUnread is not null) ScrollTo(_unreadDivider.Top);
            else if (followEnd) ScrollToEnd();
            else if (anchorId is Guid messageId && _bubbles.TryGetValue(messageId, out var anchor)) ScrollTo(anchor.Top - anchorTop);
            if (AtBottom) NewMessages?.Invoke(false);
            else if (changedConversation || _lastMessage != last) NewMessages?.Invoke(true);
            _lastMessage = last;
        }
        finally { _flow.ResumeLayout(true); _binding = false; }
        foreach (var bubble in _bubbles.Values) bubble.EnsureImagesLoaded();
    }

    private void SetPosition(Control child, int index)
    { if (_flow.Controls.GetChildIndex(child) != index) _flow.Controls.SetChildIndex(child, index); }

    private static bool IsUnread(ChatMessage message, Guid localUser) =>
        message.Kind != MessageKind.System && message.SenderUserId != localUser && !message.IsRead;

    public bool AtBottom => !_flow.VerticalScroll.Visible ||
        _flow.VerticalScroll.Value + _flow.VerticalScroll.LargeChange >= _flow.VerticalScroll.Maximum - this.ScalePixels(24);

    private void ScrollTo(int delta) => _flow.AutoScrollPosition = new Point(0, Math.Max(0, delta - _flow.AutoScrollPosition.Y));

    public void ScrollToEnd()
    {
        if (_flow.Controls.Count > 0)
            _flow.AutoScrollPosition = new Point(0, Math.Max(0, _flow.VerticalScroll.Maximum));
        NewMessages?.Invoke(false);
    }
    public void FocusMessage(Guid id)
    {
        if (!_bubbles.TryGetValue(id, out var bubble)) return;
        if (_highlight is Guid last && _bubbles.TryGetValue(last, out var old)) old.Highlighted = false;
        _flow.ScrollControlIntoView(bubble); bubble.Highlighted = true; _highlight = id;
        bubble.EnsureImagesLoaded();
    }
}

public sealed record ComposerSubmission(string Text, string[] Files, Guid? ReplyTo, Guid? EditId);

internal sealed record ComposerDraft(string Text, string[] Files, Guid? Reply, Guid? Edit, string Context);

public sealed class ChatInputControl : UserControl
{
    internal const int BaseHeight = 84;
    private const int ContextHeight = 26;
    private const int EditorHeight = 56;
    private const int PreviewHeight = 84;
    private readonly PromptTextBox _text = new() { Multiline = true, Dock = DockStyle.Fill, MaxLength = 16000, ScrollBars = ScrollBars.Vertical,
        PlaceholderText = "Nhập tin nhắn…  Enter để gửi · Shift+Enter để xuống dòng" };
    private readonly Button _send = Theme.Button("Gửi ➤");
    private readonly TableLayoutPanel _root = Theme.Stack(3);
    private readonly FlowLayoutPanel _previews = new BufferedFlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
    private readonly LinkLabel _context = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly List<string> _paths = [];
    private Guid? _replyTo, _editId;
    private int _draftGeneration;
    private bool _sending;
    public event Func<ComposerSubmission, Task>? SendRequested;
    public event Action<int>? HeightRequested;
    internal int MinimumHeight => BaseHeight + (_context.Text.Length > 0 ? ContextHeight : 0);
    public ChatInputControl()
    {
        Dock = DockStyle.Fill; Padding = new Padding(12, 8, 12, 8); BackColor = Color.White;
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var attach = Theme.Secondary("+ Đính kèm");
        attach.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        attach.AccessibleName = "Chọn tệp hoặc thư mục";
        layout.Controls.Add(attach, 0, 0);
        _send.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _send.AccessibleName = "Gửi tin nhắn";
        layout.Controls.Add(_text, 1, 0); layout.Controls.Add(_send, 2, 0);
        _root.Controls.Add(_context, 0, 0); _root.Controls.Add(_previews, 0, 1); _root.Controls.Add(layout, 0, 2); Controls.Add(_root);
        void FitButtons()
        {
            var compact = _root.ClientSize.Width < this.ScalePixels(360);
            attach.Text = compact ? "+ Tệp" : "+ Đính kèm";
            attach.MinimumSize = new Size(this.ScalePixels(compact ? 56 : 90), this.ScalePixels(36));
            _send.Text = compact ? "Gửi" : "Gửi ➤";
            _send.MinimumSize = new Size(this.ScalePixels(compact ? 56 : 90), this.ScalePixels(36));
        }
        _root.SizeChanged += (_, _) => { FitButtons(); FitPreviews(); };
        DpiChangedAfterParent += (_, _) => { FitButtons(); UpdateLayoutHeight(); };
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
        catch (Exception ex) { if (!IsDisposed) MessageBox.Show(this, UiText.HideIpAddresses(ex.Message), "Chưa gửi được", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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
        _previews.SuspendLayout();
        try
        {
            foreach (Control control in _previews.Controls.Cast<Control>().ToArray()) control.Dispose();
            foreach (var path in _paths)
            {
                var preview = new AttachmentPreviewControl(path);
                preview.RemoveRequested += () =>
                {
                    if (_sending) return;
                    _paths.Remove(path);
                    RefreshPreviews();
                };
                Theme.AddAtCurrentDpi(_previews, preview);
            }
        }
        finally { _previews.ResumeLayout(true); }
        UpdateLayoutHeight();
    }
    private void UpdateLayoutHeight()
    {
        _root.RowStyles[0].Height = this.ScalePixels(MinimumHeight - BaseHeight);
        HeightRequested?.Invoke(MinimumHeight + (_paths.Count > 0 ? PreviewHeight : 0));
        FitPreviews();
    }

    private void FitPreviews()
    {
        var available = Math.Max(0, _root.ClientSize.Height - (int)_root.RowStyles[0].Height - this.ScalePixels(EditorHeight));
        var height = _paths.Count > 0 ? Math.Min(this.ScalePixels(PreviewHeight), available) : 0;
        if (_root.RowStyles[1].Height != height) _root.RowStyles[1].Height = height;
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
    private readonly PersonNameLabel _title = Theme.NameLabel("Chào mừng đến LanChat", 17, true);
    private readonly Label _subtitle = Theme.Label("Chọn một người trong mạng nội bộ để bắt đầu trò chuyện.");
    private readonly MessageListControl _messages = new();
    private readonly ChatInputControl _input = new() { Enabled = false };
    private readonly Button _find = Theme.Secondary("Tìm tin");
    private readonly Button _info = Theme.Secondary("Thông tin");
    private readonly LinkLabel _new = new() { Text = "Tin mới ↓", AutoSize = true, Visible = false, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, BackColor = Color.White };
    public event Action<bool>? PanelRequested;
    public event Action? ReadRequested;
    private readonly LinkLabel _older = new() { Text = "Xem tin nhắn cũ hơn", Dock = DockStyle.Fill, AutoSize = true, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    public event Func<ComposerSubmission, Task>? SendRequested;
    public event Func<Task>? OlderRequested;
    public event Action<ChatMessage, string>? ActionRequested;
    public event Action<AttachmentDto>? FileRequested;
    public ChatViewControl()
    {
        Dock = DockStyle.Fill;
        var layout = Theme.Stack(5);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_title, 0, 0); header.Controls.Add(_find, 1, 0); header.Controls.Add(_info, 2, 0);
        _find.Click += (_, _) => PanelRequested?.Invoke(true); _info.Click += (_, _) => PanelRequested?.Invoke(false);
        _find.Enabled = _info.Enabled = false;
        var history = new BufferedPanel { Dock = DockStyle.Fill };
        history.Controls.Add(_messages); history.Controls.Add(_new); _new.BringToFront();
        void PositionNewMessagesLink() => _new.Location = new Point(
            Math.Max(0, history.ClientSize.Width - _new.Width - this.ScalePixels(24)),
            Math.Max(0, history.ClientSize.Height - _new.Height - this.ScalePixels(12)));
        history.SizeChanged += (_, _) => PositionNewMessagesLink();
        _new.SizeChanged += (_, _) => PositionNewMessagesLink();
        DpiChangedAfterParent += (_, _) => PositionNewMessagesLink();
        _messages.NewMessages += value => _new.Visible = value || _searchWindow;
        _messages.ReachedBottom += () => ReadRequested?.Invoke();
        _new.LinkClicked += async (_, _) => { if (_searchWindow && OlderRequested is not null) await OlderRequested(); else { _messages.ScrollToEnd(); ReadRequested?.Invoke(); } };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ChatInputControl.BaseHeight));
        _title.Padding = new Padding(14, 8, 0, 0); _subtitle.Padding = new Padding(14, 0, 0, 0); _subtitle.ForeColor = Theme.Muted;
        _title.AutoSize = false; _title.AutoEllipsis = true; _subtitle.AutoSize = false; _subtitle.AutoEllipsis = true;
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(_subtitle, 0, 1); layout.Controls.Add(_older, 0, 2); layout.Controls.Add(history, 0, 3); layout.Controls.Add(_input, 0, 4);
        Controls.Add(layout); _input.SendRequested += async text => { if (SendRequested is not null) await SendRequested(text); };
        var requestedInputHeight = ChatInputControl.BaseHeight;
        void FitComposer()
        {
            var headerHeight = (int)(layout.RowStyles[0].Height + layout.RowStyles[1].Height);
            if (_older.Visible) headerHeight += _older.PreferredHeight + _older.Margin.Vertical;

            var available = Math.Max(0, layout.ClientSize.Height - headerHeight);
            var preferred = this.ScalePixels(requestedInputHeight);
            // Keep some history visible on short screens. The preview strip can scroll.
            var minimum = Math.Min(preferred, this.ScalePixels(ChatInputControl.BaseHeight + 62));
            var limit = Math.Max(minimum, available - this.ScalePixels(76));
            limit = Math.Min(limit, Math.Max(this.ScalePixels(_input.MinimumHeight), available - this.ScalePixels(24)));
            var height = Math.Min(preferred, Math.Min(available, limit));
            if (layout.RowStyles[4].Height != height) layout.RowStyles[4].Height = height;
        }
        _input.HeightRequested += height => { requestedInputHeight = height; FitComposer(); };
        layout.SizeChanged += (_, _) => FitComposer();
        _older.VisibleChanged += (_, _) => FitComposer();
        DpiChangedAfterParent += (_, _) => FitComposer();
        _messages.ActionRequested += (m, a) => ActionRequested?.Invoke(m, a); _messages.FileRequested += f => FileRequested?.Invoke(f);
        _input.InstallDrop(this); _input.InstallDrop(_messages);
        _older.LinkClicked += async (_, _) => { if (OlderRequested is not null) await OlderRequested(); };
    }
    public void SelectConversation(string name, bool online, bool group = false) => SelectConversation(new PersonName(name), online, group);

    internal void SelectConversation(PersonName name, bool online, bool group)
    {
        _title.SetName(name);
        _subtitle.Text = group ? "Chat nhóm · Tin nhắn được lưu và đồng bộ giữa các máy" : online ? "● Đang trực tuyến · Chat trực tiếp trong mạng LAN" : "○ Đang offline · Tin nhắn sẽ tự gửi khi kết nối lại";
        _input.Enabled = true; _find.Enabled = _info.Enabled = true;
    }
    internal void Bind(Guid id, IReadOnlyList<ChatMessage> messages, Guid user, MessageDetails[] details, bool search)
    { _searchWindow = search; _messages.Bind(id, messages, user, details, search); }
    internal ComposerDraft Draft() => _input.Draft();
    internal void RestoreDraft(ComposerDraft? draft) => _input.RestoreDraft(draft);
    internal void ScrollToEnd() => _messages.ScrollToEnd();
    internal bool AtBottom => _messages.AtBottom;
    public void ConfigureImages(ChatImageService images) => _messages.ConfigureImages(images);
    public void SetContext(ChatMessage message, bool edit) => _input.SetContext(message, edit);
    public void ResetContext() => _input.ResetContext();
    public void ResetDraft() => _input.ResetDraft();
    public void ClearConversation()
    {
        _input.ResetDraft(); _input.Enabled = false; _find.Enabled = _info.Enabled = false; _messages.Bind(Guid.Empty, [], Guid.Empty, []);
        _searchWindow = false; _older.Visible = _new.Visible = false;
        _title.SetName(new PersonName("Chào mừng đến LanChat"));
        _subtitle.Text = "Chọn cuộc trò chuyện để tiếp tục.";
    }
    private bool _searchWindow;
    public void SetHistoryAvailable(bool value, bool search = false) { _searchWindow = search; _older.Visible = value; _older.Text = search ? "Quay về tin nhắn mới" : "Xem tin nhắn cũ hơn"; if (search) _new.Visible = true; }
    public void FocusMessage(Guid id) => _messages.FocusMessage(id);
#if DEBUG
    internal Task SubmitForVerificationAsync(string text) => _input.SubmitForVerificationAsync(text);
    internal void DropForVerification(string[] files) => _input.DropForVerification(files);
#endif
}
