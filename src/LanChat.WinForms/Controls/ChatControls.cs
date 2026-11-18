using System.Drawing.Drawing2D;
using System.Globalization;
using LanChat.Domain;
using LanChat.Application;
using LanChat.WinForms.Forms;
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
    private static readonly Color SentBackground = Color.FromArgb(237, 244, 255);
    private static readonly Color SentBorder = Color.FromArgb(201, 219, 245);
    private static readonly Color SystemBackground = Color.FromArgb(235, 240, 247);
    private readonly TableLayoutPanel _card = new() { ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
    private readonly PersonNameLabel _sender = Theme.NameLabel("", 9, true);
    private readonly ReplyQuoteControl _quote = new();
    private readonly Label _body = Theme.Label("");
    private readonly Label _state = Theme.Label("", 8);
    private readonly FlowLayoutPanel _files = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
    private readonly FlowLayoutPanel _pictures = new() { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
    private ChatImageService? _images;
    private AttachmentActions? _attachmentActions;
    private bool _updatingHeight;
    private readonly ContextMenuStrip _menu = new();
    private readonly ContextMenuStrip _reactionMenu = new();
    private ChatMessage? _message;
    private Presentation? _presentation;
    private sealed record Presentation(Guid Id, Guid Sender, string Text, long CreatedAt, MessageKind Kind,
        bool Edited, bool Recalled, bool Forwarded, MessageStatus Status, int ReadCount, string Name,
        string? Department, string Reactions, ReplyQuote? Quote, bool Own, bool StartsGroup, bool Unread, bool SeparateBefore, bool ShowDate);
    private readonly IconButton _reply = Theme.Icon(UiIcon.Reply, "Trả lời");
    private readonly IconButton _react = Theme.Icon(UiIcon.Heart, "Cảm xúc");
    private readonly IconButton _more = Theme.Icon(UiIcon.More, "Thao tác khác");
    private readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
    private readonly Panel _footer = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private ToolTip? _toolTips;
    private bool _own, _startsGroup, _unread, _separateBefore;
    private AttachmentDto[] _attachments = [];
    private Rectangle _bubbleBounds, _avatarBounds;
    private Color _border;
    private (Color Background, Color Ink) _avatarColor;
    private string _initials = "";
    internal Guid MessageId => _message?.Id ?? Guid.Empty;
    internal bool Highlighted { get => _highlighted; set { if (_highlighted == value) return; _highlighted = value; Invalidate(); } }
    private bool _highlighted;
    public event Action<ChatMessage, string>? ActionRequested;
    public event Action<AttachmentDto>? FileRequested;
    internal event Action<AttachmentDto, AttachmentCommand>? AttachmentCommandRequested;
    internal event Action<Guid, Guid>? QuoteRequested;
    public MessageBubbleControl()
    {
        Size = new Size(400, 120); Margin = Padding.Empty;
        BackColor = Theme.Canvas; DoubleBuffered = true; ResizeRedraw = true;
        _card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var control in new Control[] { _sender, _quote, _body, _pictures, _files, _footer })
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
        _state.Dock = _actions.Dock = DockStyle.None;
        _footer.Controls.AddRange([_state, _actions]);
        Controls.Add(_card);
        _quote.Click += (_, _) =>
        {
            if (_message is { IsRecalled: false, ReplyToMessageId: Guid id } message)
                QuoteRequested?.Invoke(message.ConversationId, id);
        };
        foreach (var (caption, action, icon) in new[] { ("Trả lời", "reply", UiIcon.Reply), ("Sao chép", "copy", UiIcon.Copy),
            ("Chuyển tiếp", "forward", UiIcon.Forward), ("Sửa", "edit", UiIcon.Edit), ("Thu hồi", "recall", UiIcon.Restore),
            ("Xóa phía tôi", "delete", UiIcon.Delete) })
        {
            var item = _menu.Items.Add(caption, UiIcons.ForMenu(icon)); item.Tag = action;
            item.Click += (_, _) => { if (_message is not null) { if (action == "copy") UiInteraction.Copy(this, _message.Text); else ActionRequested?.Invoke(_message, action); } };
        }
        var reactions = new ToolStripMenuItem("Cảm xúc", UiIcons.ForMenu(UiIcon.Heart)); reactions.Tag = "react";
        void AddReaction(string caption, string action)
        {
            void SelectReaction(object? sender, EventArgs e)
            {
                if (_message is { IsRecalled: false, Kind: not MessageKind.System } message)
                    ActionRequested?.Invoke(message, action);
            }
            var icon = action == "unreact" ? UiIcons.ForMenu(UiIcon.Close) : null;
            reactions.DropDownItems.Add(caption, icon, SelectReaction);
            _reactionMenu.Items.Add(caption, icon, SelectReaction);
        }
        foreach (var emoji in new[] { "👍", "❤️", "😂", "😮", "😢" }) AddReaction(emoji, "react:" + emoji);
        AddReaction("Gỡ cảm xúc của tôi", "unreact");
        _menu.Items.Add(reactions);
        var saveAll = _menu.Items.Add("Lưu tất cả đính kèm của tin…", UiIcons.ForMenu(UiIcon.Save));
        saveAll.Tag = "save-all";
        saveAll.Click += (_, _) => { if (_message is not null) ActionRequested?.Invoke(_message, "save-all"); };
        _menu.Opening += (_, _) => saveAll.Visible = _attachments.Length > 1;
        foreach (var button in new[] { _reply, _react, _more })
        {
            button.MinimumSize = Size.Empty; button.AutoSize = false;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Theme.Selection;
            button.Padding = Padding.Empty;
            button.Margin = Padding.Empty;
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
    internal void ConfigureAttachments(AttachmentActions actions) => _attachmentActions = actions;

    internal void AttachActionToolTips(ToolTip toolTips)
    {
        _toolTips = toolTips;
        var buttons = new[] { _reply, _react, _more };
        foreach (var button in buttons) toolTips.SetToolTip(button, button.AccessibleName);
        Disposed += (_, _) =>
        {
            foreach (var button in buttons) toolTips.SetToolTip(button, "");
            toolTips.SetToolTip(_state, "");
        };
    }

    private void UpdateLayout()
    {
        if (_updatingHeight || _message is null) return;
        _updatingHeight = true;
        try
        {
            var system = _message.Kind == MessageKind.System;
            var gutter = Math.Min(this.ScalePixels(8), Width / 8);
            var avatarSpace = !system && Width >= this.ScalePixels(130) ? this.ScalePixels(34) : 0;
            var maximum = Math.Max(1, Math.Min(Width - gutter * 2 - avatarSpace, this.ScalePixels(system ? 480 : 620)));
            var insetX = Math.Min(this.ScalePixels(10), (maximum - 1) / 2);
            var insetY = this.ScalePixels(5);
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix;
            int TextWidth(string text, Font font) => text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font,
                new Size(Math.Max(1, maximum - insetX * 2), int.MaxValue), flags).Width;
            var buttonSize = new Size(this.ScalePixels(26), this.ScalePixels(24));
            foreach (Button button in _actions.Controls) button.Size = buttonSize;
            var actionsWidth = system ? 0 : buttonSize.Width * 3;
            var natural = Math.Max(this.ScalePixels(184), Math.Min(TextWidth(_state.Text, _state.Font), this.ScalePixels(220)) + actionsWidth + this.ScalePixels(12));
            natural = Math.Max(natural, TextWidth(_body.Text, _body.Font));
            if (_startsGroup) natural = Math.Max(natural, Math.Min(TextWidth(_sender.Text, _sender.Font), this.ScalePixels(240)));
            if (_quote.Visible) natural = Math.Max(natural, this.ScalePixels(300));
            var pictureWidth = this.ScalePixels(176);
            if (_pictures.Controls.Count > 0)
            {
                pictureWidth = Math.Max(pictureWidth, _pictures.Controls.OfType<AttachmentItemControl>().Max(item => item.ActionWidth) + this.ScalePixels(6));
                natural = Math.Max(natural, Math.Min(3, _pictures.Controls.Count) * (pictureWidth + this.ScalePixels(4)));
            }
            foreach (var item in _files.Controls.OfType<AttachmentItemControl>()) natural = Math.Max(natural, item.PreferredRowWidth);
            var cardWidth = Math.Max(1, Math.Min(maximum, natural + insetX * 2));
            var contentWidth = Math.Max(1, cardWidth - insetX * 2);
            var x = system ? (Width - cardWidth) / 2 : _own ? Width - gutter - avatarSpace - cardWidth : gutter + avatarSpace;
            var y = this.ScalePixels(_startsGroup ? 8 : 2) + (_separateBefore ? 2 : 0);

            _card.SuspendLayout();
            var picturesHeight = 0;
            if (_pictures.Controls.Count > 0)
            {
                var tileWidth = Math.Max(1, Math.Min(pictureWidth, contentWidth));
                var columns = Math.Max(1, (contentWidth + this.ScalePixels(4)) / (tileWidth + this.ScalePixels(4)));
                tileWidth = Math.Max(1, Math.Min(tileWidth, (contentWidth - (columns - 1) * this.ScalePixels(4)) / columns));
                foreach (var item in _pictures.Controls.OfType<AttachmentItemControl>())
                {
                    item.Margin = new Padding(0, 0, this.ScalePixels(4), this.ScalePixels(4)); item.FitWidth(tileWidth);
                }
                var rows = (_pictures.Controls.Count + columns - 1) / columns;
                picturesHeight = rows * (_pictures.Controls[0].Height + this.ScalePixels(4));
            }
            var filesHeight = 0;
            foreach (var item in _files.Controls.OfType<AttachmentItemControl>())
            {
                item.Margin = new Padding(0, 0, 0, this.ScalePixels(4)); item.FitWidth(contentWidth);
                filesHeight += item.Height + item.Margin.Vertical;
            }
            var bodyHeight = _body.Text.Length == 0 ? 0 : TextRenderer.MeasureText(_body.Text, _body.Font,
                new Size(contentWidth, int.MaxValue), flags).Height;
            _body.Margin = new Padding(0, 0, 0, bodyHeight > 0 && _attachments.Length > 0 ? this.ScalePixels(4) : 0);
            var top = this.ScalePixels(2);
            _reply.Visible = _react.Visible = !system && contentWidth >= this.ScalePixels(174);
            actionsWidth = system ? 0 : buttonSize.Width * (_reply.Visible ? 3 : 1);
            var footerHeight = Math.Max(_state.PreferredHeight, system ? 0 : buttonSize.Height) + top;
            _actions.SetBounds(contentWidth - actionsWidth, top, actionsWidth, buttonSize.Height);
            _state.SetBounds(0, top, Math.Max(1, contentWidth - actionsWidth - (system ? 0 : this.ScalePixels(6))), footerHeight - top);
            _quote.Margin = new Padding(0, 0, 0, this.ScalePixels(4));
            var quoteHeight = _quote.HeightForWidth(contentWidth);
            var rowHeights = new[] { !system && _startsGroup ? _sender.PreferredHeight + this.ScalePixels(2) : 0,
                quoteHeight == 0 ? 0 : quoteHeight + _quote.Margin.Vertical, bodyHeight + _body.Margin.Vertical,
                picturesHeight, filesHeight, footerHeight };
            for (var i = 0; i < rowHeights.Length; i++) _card.RowStyles[i].Height = rowHeights[i];
            var height = rowHeights.Sum();
            _card.SetBounds(x + insetX, y + insetY, contentWidth, height);
            _card.ResumeLayout(true);
            _bubbleBounds = new Rectangle(x, y, cardWidth, height + insetY * 2);
            _avatarBounds = avatarSpace == 0 || !_startsGroup ? Rectangle.Empty : new Rectangle(_own ? _bubbleBounds.Right + this.ScalePixels(6) : gutter,
                y + this.ScalePixels(2), this.ScalePixels(26), this.ScalePixels(26));
            var rowHeight = Math.Max(_bubbleBounds.Bottom, _avatarBounds.Bottom) + this.ScalePixels(2);
            if (Height != rowHeight) Height = rowHeight;
            Invalidate();
        }
        finally { _updatingHeight = false; }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_separateBefore)
        {
            using var divider = new SolidBrush(Theme.Border);
            e.Graphics.FillRectangle(divider, this.ScalePixels(8), 0, Math.Max(0, Width - this.ScalePixels(16)), 1);
        }
        if (_bubbleBounds.Width <= 1 || _bubbleBounds.Height <= 1) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = _bubbleBounds;
        bounds.Width--; bounds.Height--;
        var diameter = Math.Min(this.ScalePixels(12), Math.Min(bounds.Width, bounds.Height));
        using var shape = new GraphicsPath();
        shape.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        shape.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        shape.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        shape.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        shape.CloseFigure();
        using var fill = new SolidBrush(_card.BackColor);
        using var edge = new Pen(_highlighted ? Theme.Blue : _unread ? Color.FromArgb(151, 183, 231) : _border, _highlighted ? 2 : 1);
        e.Graphics.FillPath(fill, shape);
        e.Graphics.DrawPath(edge, shape);

        if (_avatarBounds.IsEmpty) return;
        using var avatar = new SolidBrush(_avatarColor.Background);
        e.Graphics.FillEllipse(avatar, _avatarBounds);
        TextRenderer.DrawText(e.Graphics, _initials, _sender.Font, _avatarBounds, _avatarColor.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    internal void Bind(ChatMessage m, MessageDetails? details, bool own, bool startsGroup, bool unread, bool separateBefore = false)
    {
        var name = details?.SenderName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) name = own ? "Bạn" : "Người dùng";
        var files = m.IsRecalled ? Array.Empty<AttachmentDto>() : details?.Attachments ?? [];
        var sentAt = new DateTime(m.CreatedAtTicks, DateTimeKind.Utc).ToLocalTime();
        var showDate = sentAt.Date != DateTime.Today;
        var presentation = new Presentation(m.Id, m.SenderUserId, m.Text, m.CreatedAtTicks, m.Kind, m.IsEdited, m.IsRecalled,
            m.ForwardedFromMessageId.HasValue, m.Status, details?.ReadCount ?? 0, name!, details?.SenderDepartment,
            string.Join("  ", details?.Reactions ?? []), m.IsRecalled ? null : details?.Quote, own, startsGroup, unread, separateBefore, showDate);
        if (_message?.Id != m.Id) { _menu.Close(); _reactionMenu.Close(); }
        _message = m;
        if (_presentation == presentation && _attachments.SequenceEqual(files)) return;
        _presentation = presentation;
        _own = own; _startsGroup = startsGroup; _unread = unread; _separateBefore = separateBefore;
        var system = m.Kind == MessageKind.System;
        _card.SuspendLayout();
        try
        {
            _avatarColor = own ? SenderColors[0] : SenderColors[(int)((uint)m.SenderUserId.GetHashCode() % SenderColors.Length)];
            var sender = new PersonName(name!, details?.SenderDepartment);
            _sender.SetName(sender);
            _sender.ForeColor = _avatarColor.Ink;
            _initials = Initials(name!);
            AccessibleName = system ? "Thông báo nhóm" : sender.ToString() + (unread ? " · Chưa đọc" : "");
            _sender.Visible = !system && startsGroup;
            _actions.Visible = !system;
            foreach (var button in _actions.Controls.OfType<Button>()) button.Enabled = !m.IsRecalled || button == _more;
            _quote.Bind(m.IsRecalled ? null : details?.Quote, own);
            _body.Text = (m.ForwardedFromMessageId.HasValue ? "↗ Đã chuyển tiếp\n" : "") + m.Text;
            var timestamp = sentAt.ToString(showDate ? "dd/MM HH:mm" : "HH:mm");
            _state.Text = timestamp +
                (m.IsEdited ? " · Đã sửa" : "") + (own && !system ? details?.ReadCount > 0 ? " · Đã đọc" : m.Status switch
                { MessageStatus.Delivered => " · Đã nhận", MessageStatus.Synced => " · Đã đồng bộ", _ => " · Đã lưu" } : "") +
                (details is null ? "" : "  " + string.Join("  ", details.Reactions));
            _toolTips?.SetToolTip(_state, sentAt.ToString("dd/MM/yyyy HH:mm") + _state.Text.Substring(timestamp.Length));
            _state.ForeColor = Theme.Muted;
            _card.BackColor = system ? SystemBackground : own ? SentBackground : Color.White;
            _border = own && !system ? SentBorder : Theme.Border;
            foreach (Button button in _actions.Controls) { button.BackColor = _card.BackColor; button.ForeColor = Theme.Muted; }
            _body.ForeColor = system ? Theme.Muted : Theme.Ink;
            _body.TextAlign = system ? ContentAlignment.MiddleCenter : ContentAlignment.TopLeft;
            _state.TextAlign = system ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
            _actions.FlowDirection = FlowDirection.LeftToRight;
            BindAttachments(files);
            UpdateLayout();
        }
        finally { _card.ResumeLayout(true); }
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
            var item = new AttachmentItemControl(file, _images, _attachmentActions);
            item.Selected += f => FileRequested?.Invoke(f);
            item.CommandRequested += (f, command) => AttachmentCommandRequested?.Invoke(f, command);
            Theme.AddAtCurrentDpi(_images is not null && ChatImageService.IsImage(file) ? _pictures : _files, item);
        }
    }
    public void EnsureImagesLoaded() { foreach (var item in _pictures.Controls.OfType<AttachmentItemControl>()) item.EnsureImageLoaded(); }

#if DEBUG
    internal void ClickActionsForVerification(string emoji)
    {
        if (_reply.Icon != UiIcon.Reply || _react.Icon != UiIcon.Heart || !_reply.Visible || !_react.Visible)
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
    private readonly Queue<MessageBubbleControl> _prepared = new();
    private Guid? _conversation;
    private bool _resizing, _resizeQueued, _binding;
    private Guid? _firstNewMessage;
    private Guid? _lastMessage;
    private Guid? _highlight;
    public event Action<bool>? NewMessages;
    public event Action? ReachedBottom;
    private ChatImageService? _images;
    private AttachmentActions? _attachmentActions;
    public event Action<ChatMessage, string>? ActionRequested;
    public event Action<AttachmentDto>? FileRequested;
    internal event Action<AttachmentDto, AttachmentCommand>? AttachmentCommandRequested;
    internal event Action<Guid, Guid>? QuoteRequested;
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

    internal void ConfigureAttachments(AttachmentActions actions) { _attachmentActions = actions; foreach (var bubble in _bubbles.Values) bubble.ConfigureAttachments(actions); }

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
            var width = ItemWidth;
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
    private int ItemWidth
    {
        get
        {
            var scrollBar = SystemInformation.GetVerticalScrollBarWidthForDpi(this.LayoutDpi());
            var viewport = Math.Min(_flow.ClientSize.Width, _flow.Width - scrollBar);
            return Math.Max(1, viewport - _flow.Padding.Horizontal - this.ScalePixels(2));
        }
    }
    private MessageBubbleControl CreateBubble(bool hidden = false)
    {
        var bubble = new MessageBubbleControl { Visible = !hidden };
        bubble.ActionRequested += (message, action) => ActionRequested?.Invoke(message, action);
        bubble.FileRequested += file => FileRequested?.Invoke(file);
        bubble.AttachmentCommandRequested += (file, command) => AttachmentCommandRequested?.Invoke(file, command);
        if (_attachmentActions is not null) bubble.ConfigureAttachments(_attachmentActions);
        bubble.QuoteRequested += (conversationId, messageId) => QuoteRequested?.Invoke(conversationId, messageId);
        if (_images is not null) bubble.ConfigureImages(_images);
        Theme.AddAtCurrentDpi(_flow, bubble);
        bubble.AttachActionToolTips(_actionToolTips);
        return bubble;
    }

    internal async Task PrepareAsync(int count, CancellationToken ct, Func<bool>? isCurrent = null)
    {
        // Construct native windows in small batches so the first long history
        // does not monopolize the UI thread. Existing rows cover later switches.
        while (_bubbles.Count + _prepared.Count < count)
        {
            ct.ThrowIfCancellationRequested();
            if (IsDisposed || isCurrent?.Invoke() == false) return;
            for (var i = 0; i < 4 && _bubbles.Count + _prepared.Count < count; i++)
            {
                var bubble = CreateBubble(hidden: true); _prepared.Enqueue(bubble);
                CreateHandles(bubble);
            }
            await Task.Delay(1, ct);
        }
        ct.ThrowIfCancellationRequested();

        static void CreateHandles(Control control)
        {
            _ = control.Handle;
            foreach (Control child in control.Controls) CreateHandles(child);
        }
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
        var reusable = new Queue<MessageBubbleControl>();
        var width = ItemWidth;
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
            // Reuse in visual order; dictionary slot reuse can reverse the rows
            // and otherwise force a native z-order change for every message.
            foreach (var removed in _flow.Controls.OfType<MessageBubbleControl>()
                .Where(bubble => _bubbles.ContainsKey(bubble.MessageId) && !ids.Contains(bubble.MessageId)).ToArray())
            { reusable.Enqueue(removed); _bubbles.Remove(removed.MessageId); }

            ChatMessage? previous = null;
            var index = 0;
            foreach (var m in messages)
            {
                var hasDivider = m.Id == markerId;
                if (hasDivider) SetPosition(_unreadDivider, index++);
                if (!_bubbles.TryGetValue(m.Id, out var bubble))
                {
                    if (reusable.Count > 0) bubble = reusable.Dequeue();
                    else if (_prepared.Count > 0) bubble = _prepared.Dequeue();
                    else bubble = CreateBubble();
                    _bubbles.Add(m.Id, bubble);
                }
                bubble.Width = width;
                byId.TryGetValue(m.Id, out var detail);
                var startsGroup = hasDivider || previous is null || m.Kind == MessageKind.System || previous.Kind == MessageKind.System ||
                    previous.SenderUserId != m.SenderUserId || Math.Abs(m.CreatedAtTicks - previous.CreatedAtTicks) >= TimeSpan.FromMinutes(5).Ticks;
                bubble.Bind(m, detail, m.SenderUserId == localUser, startsGroup, IsUnread(m, localUser), separateBefore: previous is not null &&
                    previous.Kind != MessageKind.System && m.Kind != MessageKind.System && previous.SenderUserId != m.SenderUserId);
                bubble.Visible = true;
                bubble.Highlighted = m.Id == _highlight;
                SetPosition(bubble, index++);
                previous = m;
            }
            while (reusable.Count > 0) reusable.Dequeue().Dispose();
            while (_prepared.Count > 0) _prepared.Dequeue().Dispose();
            ResizeItems();
            _flow.ResumeLayout(true);

            if (changedConversation && firstUnread is not null) ScrollTo(_unreadDivider.Top);
            else if (followEnd) ScrollToEnd();
            else if (anchorId is Guid messageId && _bubbles.TryGetValue(messageId, out var anchor)) ScrollTo(anchor.Top - anchorTop);
            if (AtBottom) NewMessages?.Invoke(false);
            else if (changedConversation || _lastMessage != last) NewMessages?.Invoke(true);
            _lastMessage = last;
        }
        finally
        {
            while (reusable.Count > 0) reusable.Dequeue().Dispose();
            _flow.ResumeLayout(true); _binding = false;
        }
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
    internal bool FocusMessage(Guid id)
    {
        if (!_bubbles.TryGetValue(id, out var bubble)) return false;
        ClearHighlight();
        _flow.ScrollControlIntoView(bubble); bubble.Highlighted = true; _highlight = id;
        bubble.EnsureImagesLoaded();
        return true;
    }

    internal void ClearHighlight()
    {
        if (_highlight is Guid last && _bubbles.TryGetValue(last, out var old)) old.Highlighted = false;
        _highlight = null;
    }
}

public sealed record ComposerSubmission(string Text, string[] Files, Guid? ReplyTo, Guid? EditId);

internal sealed record ComposerDraft(string Text, string[] Files, Guid? Reply, Guid? Edit, string Context, ComposerDraft? BeforeEdit = null)
{
    public bool HasContent => Text.Length > 0 || Files.Length > 0 || Reply.HasValue || Edit.HasValue;
    public IEnumerable<string> AllFiles => Files.Concat(BeforeEdit?.AllFiles ?? []);
}

public sealed class ChatInputControl : UserControl
{
    internal const int BaseHeight = 92;
    private const int ContextHeight = 26;
    private const int EditorHeight = 80;
    private const int PreviewHeight = 84;
    private const int MaxFiles = 20;
    private readonly PromptTextBox _text = new() { Multiline = true, Dock = DockStyle.Fill, MaxLength = 16000, ScrollBars = ScrollBars.Vertical,
        PlaceholderText = "Nhập tin nhắn…  Enter để gửi · Shift+Enter để xuống dòng" };
    private readonly Button _send = Theme.Button("Gửi", UiIcon.Send);
    private readonly TableLayoutPanel _root = Theme.Stack(4);
    private readonly Label _feedback = Theme.Label("", 9);
    private readonly ToolTip _feedbackTip = new();
    private readonly FlowLayoutPanel _previews = new BufferedFlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false };
    private readonly LinkLabel _context = new IconLinkLabel(UiIcon.Close) { Dock = DockStyle.Fill, AutoEllipsis = true, AutoSize = false };
    private readonly List<string> _paths = [];
    private readonly Button _attach = Theme.Secondary("Đính kèm", UiIcon.Attach);
    private readonly Button _pasteImage = Theme.Secondary("Dán ảnh", UiIcon.Paste);
    private readonly Button _captureRegion = Theme.Secondary("Chụp vùng", UiIcon.Capture);
    private DraftImageStore? _draftImages;
    private CancellationToken _draftLifetime;
    private Guid? _replyTo, _editId;
    private ComposerDraft? _beforeEdit;
    private int _draftGeneration;
    private bool _sending, _processingImage;
    public event Func<ComposerSubmission, Task>? SendRequested;
    public event Action<int>? HeightRequested;
    internal event Action? DraftFilesChanged;
    internal bool IsWorking => _sending || _processingImage;
    internal void FocusEditor() { if (_text.CanFocus && !IsWorking) _text.Focus(); }
    private bool CanAttach => Enabled && !IsWorking && !_editId.HasValue;
    internal int MinimumHeight => BaseHeight + (_context.Text.Length > 0 ? ContextHeight : 0) + (_feedback.Text.Length > 0 ? ContextHeight : 0);
    public ChatInputControl()
    {
        Dock = DockStyle.Fill; Padding = new Padding(10, 6, 10, 6); BackColor = Color.White;
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty, WrapContents = false };
        _attach.AccessibleName = "Chọn tệp hoặc thư mục";
        _pasteImage.AccessibleName = "Dán ảnh";
        _captureRegion.AccessibleName = "Chụp vùng";
        var toolTips = new ToolTip();
        toolTips.SetToolTip(_attach, _attach.AccessibleName);
        toolTips.SetToolTip(_pasteImage, "Dán ảnh từ bảng nhớ tạm");
        toolTips.SetToolTip(_captureRegion, "Chụp một vùng màn hình · Esc để hủy");
        tools.Controls.AddRange([_attach, _pasteImage, _captureRegion]);
        layout.Controls.Add(tools, 0, 0);
        layout.SetColumnSpan(tools, 2);
        _send.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _send.AccessibleName = "Gửi tin nhắn";
        layout.Controls.Add(_text, 0, 1); layout.Controls.Add(_send, 1, 1);
        _feedback.AutoSize = false; _feedback.AutoEllipsis = true; _feedback.UseMnemonic = false;
        _feedback.ForeColor = Color.Firebrick; _feedback.AccessibleRole = AccessibleRole.StatusBar;
        _root.Controls.Add(_context, 0, 0); _root.Controls.Add(_previews, 0, 1); _root.Controls.Add(_feedback, 0, 2); _root.Controls.Add(layout, 0, 3); Controls.Add(_root);
        void FitButtons()
        {
            var compact = _root.ClientSize.Width < this.ScalePixels(360);
            FitAction(_attach, "Đính kèm");
            FitAction(_pasteImage, "Dán ảnh");
            FitAction(_captureRegion, "Chụp vùng");
            _send.MinimumSize = new Size(this.ScalePixels(compact ? 56 : 80), this.ScalePixels(32));

            void FitAction(Button button, string caption)
            {
                button.Text = compact ? "" : caption;
                button.MinimumSize = new Size(this.ScalePixels(compact ? 32 : 75), this.ScalePixels(29));
                button.Margin = new Padding(0, 0, this.ScalePixels(4), this.ScalePixels(2));
                button.Padding = new Padding(this.ScalePixels(compact ? 3 : 6), 0, this.ScalePixels(compact ? 3 : 6), 0);
            }
        }
        _root.SizeChanged += (_, _) => { FitButtons(); FitPreviews(); };
        DpiChangedAfterParent += (_, _) => { FitButtons(); UpdateLayoutHeight(); };
        var attachments = new ContextMenuStrip();
        attachments.Items.Add("Thêm tệp…", UiIcons.ForMenu(UiIcon.File), (_, _) =>
        {
            using var dialog = new OpenFileDialog { Multiselect = true, Title = "Chọn tệp đính kèm", Filter = "Tất cả tệp|*.*" };
            if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles(dialog.FileNames);
        });
        attachments.Items.Add("Thêm thư mục…", UiIcons.ForMenu(UiIcon.Folder), (_, _) => SelectFolder());
        _attach.Click += (_, _) => { if (CanAttach) attachments.Show(_attach, new Point(0, _attach.Height)); };
        _pasteImage.Click += async (_, _) => await PrepareImageAsync(() =>
            Clipboard.GetDataObject() is { } data ? ClipboardDraftImage.Read(data) : null, "Anh-dan", "Không có ảnh PNG hoặc bitmap trong bảng nhớ tạm.");
        _captureRegion.Click += async (_, _) => await PrepareImageAsync(() =>
            (TopLevelControl as Form ?? FindForm()) is { } owner ? RegionCaptureForm.CaptureRegion(owner) : null, "Anh-chup");
        Disposed += (_, _) => { attachments.Dispose(); toolTips.Dispose(); _feedbackTip.Dispose(); };
        _context.LinkClicked += (_, _) => { if (!_sending) ResetContext(); };
        InstallDrop(this); InstallDrop(_text); InstallDrop(_previews);
        _send.Click += async (_, _) => await SendAsync();
        _text.Margin = new Padding(3, 4, 3, 5);
        _text.AccessibleName = "Nhập tin nhắn";
        _text.TryPasteImage = TryPasteImage;
        _text.TextChanged += (_, _) => UpdateButtons();
        _text.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift && !_text.IsComposing) { e.SuppressKeyPress = true; await SendAsync(); } };
        FitButtons();
        UpdateButtons();
    }

    internal void ConfigureDraftImages(DraftImageStore store, CancellationToken lifetime)
    {
        _draftImages = store;
        _draftLifetime = lifetime;
        UpdateButtons();
    }

    private bool TryPasteImage()
    {
        if (!CanAttach || _draftImages is null) return false;
        try
        {
            if (Clipboard.GetDataObject() is not { } data || !ClipboardDraftImage.PreferImageOnPaste(data)) return false;
            _ = PrepareImageAsync(() => ClipboardDraftImage.Read(data), "Anh-dan", "Không đọc được ảnh trong bảng nhớ tạm.");
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }

    private async Task PrepareImageAsync(Func<Bitmap?> read, string prefix, string? missing = null)
    {
        if (!CanAttach || _draftImages is not { } store) return;
        if (_paths.Count >= MaxFiles)
        {
            ShowImageIssue("Mỗi tin nhắn đính kèm tối đa 20 tệp.");
            return;
        }

        var generation = _draftGeneration;
        _processingImage = true;
        UpdateButtons();
        try
        {
            using var image = read();
            if (image is null)
            {
                if (missing is not null) ShowImageIssue(missing);
                return;
            }
            var staged = await store.StageAsync(image, prefix, _draftLifetime);
            if (IsDisposed || _draftLifetime.IsCancellationRequested || generation != _draftGeneration || !Enabled)
            {
                store.Discard(staged.Path, staged.Thumbnail);
                return;
            }
            store.Keep(staged.Path, staged.Thumbnail);
            _paths.Add(staged.Path);
            RefreshPreviews();
            _text.Focus();
        }
        catch (OperationCanceledException) when (_draftLifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) ShowImageIssue(UiText.HideIpAddresses(ex.Message)); }
        finally
        {
            _processingImage = false;
            if (!IsDisposed)
            {
                UpdateButtons();
                DraftFilesChanged?.Invoke();
            }
        }
    }

    private void ShowImageIssue(string message) => SetFeedback(message);

    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && !IsWorking && (_editId.HasValue || _replyTo.HasValue))
        { ResetContext(); FocusEditor(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void SetFeedback(string message)
    {
        _feedback.Text = message; _feedbackTip.SetToolTip(_feedback, message); UpdateLayoutHeight();
    }

    private void UpdateButtons()
    {
        var canAttach = !IsWorking && !_editId.HasValue;
        _attach.Enabled = canAttach;
        _pasteImage.Enabled = _captureRegion.Enabled = canAttach && _draftImages is not null;
        _send.Enabled = !IsWorking && (!string.IsNullOrWhiteSpace(_text.Text) || (!_editId.HasValue && _paths.Count > 0));
        _send.Text = _sending ? "Đang gửi…" : _editId.HasValue ? "Lưu sửa" : "Gửi";
    }

    private void SelectFolder()
    {
        var owner = TopLevelControl ?? this;
        try
        {
            using var dialog = new FolderPickerDialog();
            if (dialog.ShowDialog(owner) == DialogResult.OK && dialog.SelectedPath is string path) AddFiles([path]);
        }
        catch (Exception ex)
        {
            UiInteraction.Warn(this, ex.Message, "Không thêm được thư mục");
        }
    }

    private async Task SendAsync()
    {
        if (IsWorking || !_send.Enabled || (string.IsNullOrWhiteSpace(_text.Text) && _paths.Count == 0) || SendRequested is null) return;
        var generation = _draftGeneration;
        SetFeedback("");
        _sending = true; UpdateButtons(); _text.ReadOnly = true;
        try
        {
            var submission = new ComposerSubmission(_text.Text, _paths.ToArray(), _replyTo, _editId);
            await SendRequested(submission);
            if (!IsDisposed && generation == _draftGeneration) CompleteSubmission(submission);
        }
        catch (OperationCanceledException) when (_draftLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsDisposed) SetFeedback((generation == _draftGeneration ? "Chưa gửi được: " :
                "Tin ở hội thoại trước chưa gửi được; bản nháp vẫn được giữ: ") + UiText.HideIpAddresses(ex.Message));
        }
        finally
        {
            _sending = false;
            if (!IsDisposed)
            {
                UpdateButtons();
                _text.ReadOnly = false;
                DraftFilesChanged?.Invoke();
            }
        }
    }
    public void InstallDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) => e.Effect = CanAttach && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        control.DragDrop += (_, e) => { if (CanAttach && e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths); };
    }
    public void AddFiles(IEnumerable<string> paths)
    {
        if (!CanAttach) return;
        SetFeedback("");
        var missing = 0; var overflow = 0; var added = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path) && !Directory.Exists(path)) { missing++; continue; }
            if (_paths.Contains(path, StringComparer.OrdinalIgnoreCase)) continue;
            if (_paths.Count >= MaxFiles) { overflow++; continue; }
            _paths.Add(path); added++;
        }
        RefreshPreviews();
        UpdateButtons();
        if (missing > 0 || overflow > 0) ShowImageIssue($"Đã thêm {added} mục." +
            (missing > 0 ? $" {missing} mục không còn tồn tại hoặc không truy cập được." : "") +
            (overflow > 0 ? $" {overflow} mục vượt giới hạn 20 tệp mỗi tin nhắn." : ""));
    }
    private void RefreshPreviews()
    {
        _previews.SuspendLayout();
        try
        {
            foreach (Control control in _previews.Controls.Cast<Control>().ToArray()) control.Dispose();
            foreach (var path in _paths)
            {
                var preview = new AttachmentPreviewControl(path, _draftImages?.Thumbnail(path));
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
        UpdateButtons();
        DraftFilesChanged?.Invoke();
    }
    private void UpdateLayoutHeight()
    {
        _root.RowStyles[0].Height = this.ScalePixels(_context.Text.Length > 0 ? ContextHeight : 0);
        _root.RowStyles[2].Height = this.ScalePixels(_feedback.Text.Length > 0 ? ContextHeight : 0);
        HeightRequested?.Invoke(MinimumHeight + (_paths.Count > 0 ? PreviewHeight : 0));
        FitPreviews();
    }

    private void FitPreviews()
    {
        var available = Math.Max(0, _root.ClientSize.Height - (int)(_root.RowStyles[0].Height + _root.RowStyles[2].Height) - this.ScalePixels(EditorHeight));
        var height = _paths.Count > 0 ? Math.Min(this.ScalePixels(PreviewHeight), available) : 0;
        if (_root.RowStyles[1].Height != height) _root.RowStyles[1].Height = height;
    }
    public void SetContext(ChatMessage message, bool edit)
    {
        if (IsWorking) return;
        if (edit && !_editId.HasValue) _beforeEdit = Draft();
        if (!edit && _editId.HasValue) ResetContext();
        _replyTo = edit ? null : message.Id; _editId = edit ? message.Id : null;
        _context.Text = (edit ? "Sửa: " : "Trả lời: ") + message.Text.NormalizeLines(" ") + "  · Bấm để hủy";
        if (edit) { _paths.Clear(); RefreshPreviews(); _text.Text = message.Text; }
        UpdateButtons();
        UpdateLayoutHeight(); _text.Focus();
    }
    public void ResetContext()
    {
        if (_editId.HasValue) { var draft = _beforeEdit; RestoreDraft(draft); return; }
        _replyTo = null; _context.Text = ""; UpdateButtons(); UpdateLayoutHeight();
    }
    internal void CompleteSubmission(ComposerSubmission submission)
    {
        if (submission.EditId.HasValue) ResetContext();
        else ResetDraft();
    }
    public void ResetDraft() => RestoreDraft(null);
    internal ComposerDraft Draft() => new(_text.Text, _paths.ToArray(), _replyTo, _editId, _context.Text, _beforeEdit);
    internal void RestoreDraft(ComposerDraft? draft)
    {
        _draftGeneration++;
        _beforeEdit = draft?.BeforeEdit;
        SetFeedback("");
        _text.Text = draft?.Text ?? ""; _paths.Clear();
        if (draft is not null) _paths.AddRange(draft.Files);
        _replyTo = draft?.Reply; _editId = draft?.Edit; _context.Text = draft?.Context ?? "";
        UpdateButtons();
        RefreshPreviews();
    }
#if DEBUG
    internal async Task SubmitForVerificationAsync(string text) { _text.Text = text; await SendAsync(); }
    internal Task AddImageForVerificationAsync(Func<Bitmap?> read) => PrepareImageAsync(read, "Anh-thu");
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
    private readonly PersonNameLabel _title = Theme.NameLabel("Chào mừng đến LanChat", 14, true);
    private readonly Label _subtitle = Theme.Label("Chọn một người trong mạng nội bộ để bắt đầu trò chuyện.");
    private readonly MessageListControl _messages = new();
    private readonly Label _loadingView = new() { Dock = DockStyle.Fill, BackColor = Theme.Canvas,
        TextAlign = ContentAlignment.MiddleCenter, Text = "Đang tải tin nhắn…", Visible = false };
    private bool _loading;
    private readonly ChatInputControl _input = new() { Enabled = false };
    private readonly Button _find = Theme.Secondary("Tìm tin", UiIcon.Search);
    private readonly Button _info = Theme.Secondary("Thông tin", UiIcon.Info);
    private readonly LinkLabel _new = new IconLinkLabel(UiIcon.Down)
        { Text = "Tin mới", Visible = false, Anchor = AnchorStyles.Right | AnchorStyles.Bottom, BackColor = Color.White };
    public event Action<bool>? PanelRequested;
    public event Action? ReadRequested;
    private readonly IconLinkLabel _older = new(UiIcon.Up)
        { Text = "Xem tin nhắn cũ hơn", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    public event Func<ComposerSubmission, Task>? SendRequested;
    public event Func<Task>? OlderRequested;
    public event Action<ChatMessage, string>? ActionRequested;
    public event Action<AttachmentDto>? FileRequested;
    internal event Action<AttachmentDto, AttachmentCommand>? AttachmentCommandRequested;
    internal event Action<Guid, Guid>? QuoteRequested;
    internal event Action? DraftFilesChanged;
    public ChatViewControl()
    {
        Dock = DockStyle.Fill;
        var layout = Theme.Stack(5);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_title, 0, 0); header.Controls.Add(_find, 1, 0); header.Controls.Add(_info, 2, 0);
        _find.AccessibleName = "Tìm tin";
        _info.AccessibleName = "Thông tin";
        var headerTips = new ToolTip();
        headerTips.SetToolTip(_find, _find.AccessibleName);
        headerTips.SetToolTip(_info, _info.AccessibleName);
        Disposed += (_, _) => headerTips.Dispose();
        void FitHeaderActions()
        {
            var compact = header.ClientSize.Width < this.ScalePixels(315);
            _find.Text = compact ? "" : _find.AccessibleName;
            _info.Text = compact ? "" : _info.AccessibleName;
            _find.MinimumSize = _info.MinimumSize = new Size(this.ScalePixels(compact ? 32 : 78), this.ScalePixels(30));
            var inset = this.ScalePixels(compact ? 3 : 8);
            _find.Padding = _info.Padding = new Padding(inset, this.ScalePixels(3), inset, this.ScalePixels(3));
        }
        header.SizeChanged += (_, _) => FitHeaderActions();
        DpiChangedAfterParent += (_, _) => FitHeaderActions();
        _find.Click += (_, _) => PanelRequested?.Invoke(true); _info.Click += (_, _) => PanelRequested?.Invoke(false);
        _find.Enabled = _info.Enabled = false;
        var history = new BufferedPanel { Dock = DockStyle.Fill };
        history.Controls.Add(_messages); history.Controls.Add(_new); history.Controls.Add(_loadingView); _new.BringToFront();
        void PositionNewMessagesLink() => _new.Location = new Point(
            Math.Max(0, history.ClientSize.Width - _new.Width - this.ScalePixels(24)),
            Math.Max(0, history.ClientSize.Height - _new.Height - this.ScalePixels(12)));
        history.SizeChanged += (_, _) => PositionNewMessagesLink();
        _new.SizeChanged += (_, _) => PositionNewMessagesLink();
        DpiChangedAfterParent += (_, _) => PositionNewMessagesLink();
        _messages.NewMessages += value => _new.Visible = value || _returnToLatest;
        _messages.ReachedBottom += () => ReadRequested?.Invoke();
        _new.LinkClicked += async (_, _) => { if (_returnToLatest && OlderRequested is not null) await OlderRequested(); else { _messages.ScrollToEnd(); ReadRequested?.Invoke(); } };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ChatInputControl.BaseHeight));
        _title.Padding = new Padding(10, 4, 0, 0); _subtitle.Padding = new Padding(10, 0, 0, 0); _subtitle.ForeColor = Theme.Muted;
        _title.AutoSize = false; _title.AutoEllipsis = true; _subtitle.AutoSize = false; _subtitle.AutoEllipsis = true;
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(_subtitle, 0, 1); layout.Controls.Add(_older, 0, 2); layout.Controls.Add(history, 0, 3); layout.Controls.Add(_input, 0, 4);
        Controls.Add(layout); _input.SendRequested += async text => { if (SendRequested is not null) await SendRequested(text); };
        _input.DraftFilesChanged += () => DraftFilesChanged?.Invoke();
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
        _messages.AttachmentCommandRequested += (f, command) => AttachmentCommandRequested?.Invoke(f, command);
        _messages.QuoteRequested += (conversationId, messageId) => QuoteRequested?.Invoke(conversationId, messageId);
        _input.InstallDrop(this); _input.InstallDrop(_messages);
        _older.LinkClicked += async (_, _) => { if (OlderRequested is not null) await OlderRequested(); };
    }
    public void SelectConversation(string name, bool online, bool group = false) => SelectConversation(new PersonName(name), online, group);

    internal void SelectConversation(PersonName name, bool online, bool group)
    {
        _title.SetName(name);
        _subtitle.Text = group ? "Cuộc trò chuyện nhóm" : online ? "● Đang trực tuyến" : "○ Ngoại tuyến · Tin nhắn sẽ gửi khi kết nối lại";
        _input.Enabled = _find.Enabled = _info.Enabled = !_loading;
    }
    internal Task PrepareHistoryAsync(int count, CancellationToken ct, Func<bool>? isCurrent = null) => _messages.PrepareAsync(count, ct, isCurrent);
    internal void SetLoading(bool loading)
    {
        _loading = loading; _loadingView.Text = "Đang tải tin nhắn…"; _loadingView.Visible = loading;
        _input.Enabled = _find.Enabled = _info.Enabled = _messages.Enabled = !loading;
        if (loading) _loadingView.BringToFront(); else _new.BringToFront();
    }
    internal void ShowHistoryError() => _loadingView.Text = "Chưa tải được tin nhắn. Bấm lại hội thoại để thử lại.";
    internal void Bind(Guid id, IReadOnlyList<ChatMessage> messages, Guid user, MessageDetails[] details, bool search)
        => _messages.Bind(id, messages, user, details, search);
    internal ComposerDraft Draft() => _input.Draft();
    internal bool ComposerWorking => _input.IsWorking;
    internal void ConfigureDraftImages(DraftImageStore store, CancellationToken lifetime) => _input.ConfigureDraftImages(store, lifetime);
    internal void RestoreDraft(ComposerDraft? draft) => _input.RestoreDraft(draft);
    internal void CompleteSubmission(ComposerSubmission submission) => _input.CompleteSubmission(submission);
    internal void FocusComposer() => _input.FocusEditor();
    internal void ScrollToEnd() => _messages.ScrollToEnd();
    internal bool AtBottom => _messages.AtBottom;
    public void ConfigureImages(ChatImageService images) => _messages.ConfigureImages(images);
    internal void ConfigureAttachments(AttachmentActions actions) => _messages.ConfigureAttachments(actions);
    public void SetContext(ChatMessage message, bool edit) => _input.SetContext(message, edit);
    public void ResetContext() => _input.ResetContext();
    public void ResetDraft() => _input.ResetDraft();
    public void ClearConversation()
    {
        SetLoading(false);
        _input.ResetDraft(); _input.Enabled = false; _find.Enabled = _info.Enabled = false; _messages.Bind(Guid.Empty, [], Guid.Empty, []);
        _returnToLatest = false; _older.Visible = _new.Visible = false;
        _title.SetName(new PersonName("Chào mừng đến LanChat"));
        _subtitle.Text = "Chọn cuộc trò chuyện để tiếp tục.";
    }
    private bool _returnToLatest;
    public void SetHistoryAvailable(bool older, bool returnToLatest = false)
    {
        _returnToLatest = returnToLatest;
        _older.Visible = older || returnToLatest;
        _older.Text = returnToLatest ? "Quay về tin nhắn mới" : "Xem tin nhắn cũ hơn";
        _older.Icon = returnToLatest ? UiIcon.Down : UiIcon.Up;
        _new.Text = returnToLatest ? "Quay về tin nhắn mới" : "Tin mới";
        if (returnToLatest || _messages.AtBottom) _new.Visible = returnToLatest;
    }
    internal bool FocusMessage(Guid id) => _messages.FocusMessage(id);
    internal void ClearHighlight() => _messages.ClearHighlight();
#if DEBUG
    internal Task SubmitForVerificationAsync(string text) => _input.SubmitForVerificationAsync(text);
    internal void DropForVerification(string[] files) => _input.DropForVerification(files);
#endif
}
