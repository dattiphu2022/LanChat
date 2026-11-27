using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LanChat.WinForms.Controls;

internal sealed class PromptTextBox : TextBox
{
    private const int SetCueBanner = 0x1501;
    private const int StartComposition = 0x010D;
    private const int EndComposition = 0x010E;
    private const int PasteMessage = 0x0302;
    private const int SetSelection = 0x00B1;
    private string _placeholderText = "";
    private bool _wasEmpty = true;
    private Match[] _emoji = [];
    internal bool ShowColorEmoji { get; set; }
    public string PlaceholderText
    {
        get => _placeholderText;
        set { _placeholderText = value; ApplyPrompt(); Invalidate(); }
    }

    internal bool IsComposing { get; private set; }
    internal event EventHandler? CompositionChanged;
    internal event EventHandler? SelectionSet;
    internal bool IsUndoing { get; private set; }
    internal Func<Keys, bool>? InterceptKey { get; set; }
    internal Func<bool>? TryPasteImage { get; set; }

    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
    {
        if (InterceptKey?.Invoke(keyData) == true) return true;
        if ((keyData == (Keys.Control | Keys.V) || keyData == (Keys.Shift | Keys.Insert)) && TryPasteImage?.Invoke() == true)
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if (m.Msg == PasteMessage && TryPasteImage?.Invoke() == true) return;
        if (m.Msg == StartComposition) IsComposing = true;
        if (m.Msg == EndComposition) IsComposing = false;
        var previousUndo = IsUndoing;
        IsUndoing = previousUndo || m.Msg is 0x0304 or 0x00C7;
        try { base.WndProc(ref m); } finally { IsUndoing = previousUndo; }
        if (m.Msg is StartComposition or EndComposition)
        {
            if (Multiline && _placeholderText.Length > 0) Invalidate();
            CompositionChanged?.Invoke(this, EventArgs.Empty);
        }
        if (m.Msg == SetSelection) SelectionSet?.Invoke(this, EventArgs.Empty);
        if (!IsComposing && _emoji is { Length: > 0 } && WindowsEmojiGlyphs.IsPaintMessage(m.Msg))
            WindowsEmojiGlyphs.PaintTextBox(this, m.WParam, _emoji, _ => (ForeColor, BackColor));
        if (WindowsEmojiGlyphs.IsPaintMessage(m.Msg) && Multiline && !IsComposing && TextLength == 0 && _placeholderText.Length > 0)
        {
            using var graphics = m.WParam == IntPtr.Zero ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(m.WParam);
            var inset = this.ScalePixels(4);
            var bounds = Rectangle.Inflate(ClientRectangle, -inset, -inset);
            if (bounds.Width > 0 && bounds.Height > 0)
                TextRenderer.DrawText(graphics, _placeholderText, Font, bounds, Theme.Muted,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | (WordWrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine));
        }
    }
    protected override void OnTextChanged(EventArgs e)
    {
        if (ShowColorEmoji) _emoji = WindowsEmoji.Sequences.Matches(Text).Cast<Match>().ToArray();
        var empty = TextLength == 0;
        if (Multiline && _placeholderText.Length > 0 && empty != _wasEmpty) Invalidate();
        _wasEmpty = empty;
        base.OnTextChanged(e);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyPrompt(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { IsComposing = false; base.OnLostFocus(e); Invalidate(); }
    private void ApplyPrompt()
    {
        if (IsHandleCreated && !Multiline) SendMessage(Handle, SetCueBanner, new IntPtr(1), _placeholderText);
    }
    internal void ReplaceSelectionWithUndo(string text) => SendMessage(Handle, 0x00C2, new IntPtr(1), text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr parameter, string text);
}
