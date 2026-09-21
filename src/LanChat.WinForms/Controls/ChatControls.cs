using LanChat.Domain;
using ChatMessage = LanChat.Domain.Message;

namespace LanChat.WinForms.Controls;

public sealed class MessageBubbleControl : UserControl
{
    private readonly Label _body = Theme.Label("");
    private readonly Label _state = Theme.Label("", 8);
    public MessageBubbleControl()
    {
        Size = new Size(400, 120); Padding = new Padding(12, 8, 12, 8);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        _body.AutoSize = false; _state.AutoSize = false;
        layout.Controls.Add(_body, 0, 0); layout.Controls.Add(_state, 0, 1); Controls.Add(layout);
        var menu = new ContextMenuStrip(); menu.Items.Add("Sao chép", null, (_, _) => { if (_body.Text.Length > 0) Clipboard.SetText(_body.Text); });
        ContextMenuStrip = _body.ContextMenuStrip = menu;
        Resize += (_, _) => UpdateHeight();
    }
    private void UpdateHeight()
    {
        var measured = TextRenderer.MeasureText(_body.Text, _body.Font, new Size(Math.Max(100, Width - Padding.Horizontal - 40), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
        var height = measured.Height + Padding.Vertical + 62;
        if (Height != height) Height = height;
    }
    public void Bind(ChatMessage m, bool own)
    {
        _body.Text = m.Text;
        _state.Text = new DateTime(m.CreatedAtTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM HH:mm") +
            (own ? m.Status == MessageStatus.Delivered ? "  ✓✓ Đã nhận" : "  ◷ Chưa đồng bộ" : "");
        _state.ForeColor = Theme.Muted;
        Controls[0].BackColor = own ? Color.FromArgb(221, 236, 255) : Color.White;
        _body.TextAlign = own ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft;
        _state.TextAlign = _body.TextAlign;
        Padding = own ? new Padding(60, 6, 14, 6) : new Padding(14, 6, 60, 6);
        UpdateHeight();
    }
}

public sealed class MessageListControl : UserControl
{
    private readonly FlowLayoutPanel _flow = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Canvas };
    private readonly Dictionary<Guid, MessageBubbleControl> _bubbles = [];
    private Guid? _conversation;
    private bool _resizing;
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
                bubble = new MessageBubbleControl(); _bubbles.Add(m.Id, bubble); _flow.Controls.Add(bubble); newMessage = true;
            }
            bubble.Bind(m, m.SenderUserId == localUser); _flow.Controls.SetChildIndex(bubble, i);
        }
        ResizeItems(); _flow.ResumeLayout(true);
        if ((changedConversation || nearBottom) && newMessage && messages.Count > 0) _flow.ScrollControlIntoView(_bubbles[messages[^1].Id]);
    }
}

public sealed class ChatInputControl : UserControl
{
    private readonly TextBox _text = new() { Multiline = true, Dock = DockStyle.Fill, MaxLength = 16000, ScrollBars = ScrollBars.Vertical,
        PlaceholderText = "Nhập tin nhắn…  Enter để gửi · Shift+Enter để xuống dòng" };
    private readonly Button _send = Theme.Button("Gửi ➤");
    public event Func<string, Task>? SendRequested;
    public ChatInputControl()
    {
        Dock = DockStyle.Fill; Padding = new Padding(12); BackColor = Color.White;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        layout.Controls.Add(_text, 0, 0); layout.Controls.Add(_send, 1, 0); Controls.Add(layout);
        _send.Click += async (_, _) => await SendAsync();
        _text.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; await SendAsync(); } };
    }
    private async Task SendAsync()
    {
        if (!_send.Enabled || string.IsNullOrWhiteSpace(_text.Text) || SendRequested is null) return;
        _send.Enabled = false; _text.ReadOnly = true;
        try { await SendRequested(_text.Text); _text.Clear(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Chưa gửi được", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _send.Enabled = true; _text.ReadOnly = false; _text.Focus(); }
    }
#if DEBUG
    internal async Task SubmitForVerificationAsync(string text) { _text.Text = text; await SendAsync(); }
#endif
}

public sealed class ChatViewControl : UserControl
{
    private readonly Label _title = Theme.Label("Chào mừng đến LanChat", 17, true);
    private readonly Label _subtitle = Theme.Label("Chọn một người trong mạng nội bộ để bắt đầu trò chuyện.");
    private readonly MessageListControl _messages = new();
    private readonly ChatInputControl _input = new() { Enabled = false };
    private readonly LinkLabel _older = new() { Text = "Xem tin nhắn cũ hơn", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    public event Func<string, Task>? SendRequested;
    public event Func<Task>? OlderRequested;
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
        _older.LinkClicked += async (_, _) => { if (OlderRequested is not null) await OlderRequested(); };
    }
    public void SelectConversation(string name, bool online)
    {
        _title.Text = name; _subtitle.Text = online ? "● Đang trực tuyến · Chat trực tiếp trong mạng LAN" : "○ Đang offline · Tin nhắn sẽ tự gửi khi kết nối lại";
        _input.Enabled = true;
    }
    public void Bind(Guid id, IReadOnlyList<ChatMessage> messages, Guid user) => _messages.Bind(id, messages, user);
    public void SetHistoryAvailable(bool value) => _older.Visible = value;
#if DEBUG
    internal Task SubmitForVerificationAsync(string text) => _input.SubmitForVerificationAsync(text);
#endif
}
