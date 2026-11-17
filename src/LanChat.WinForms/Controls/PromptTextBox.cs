using System.Runtime.InteropServices;

namespace LanChat.WinForms.Controls;

internal sealed class PromptTextBox : TextBox
{
    private const int SetCueBanner = 0x1501;
    private const int PaintMessage = 0x000F;
    private const int StartComposition = 0x010D;
    private const int EndComposition = 0x010E;
    private const int PasteMessage = 0x0302;
    private string _placeholderText = "";
    public string PlaceholderText
    {
        get => _placeholderText;
        set { _placeholderText = value; ApplyPrompt(); Invalidate(); }
    }

    internal bool IsComposing { get; private set; }
    internal Func<bool>? TryPasteImage { get; set; }

    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
    {
        if ((keyData == (Keys.Control | Keys.V) || keyData == (Keys.Shift | Keys.Insert)) && TryPasteImage?.Invoke() == true)
            return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if (m.Msg == PasteMessage && TryPasteImage?.Invoke() == true) return;
        if (m.Msg == StartComposition) IsComposing = true;
        if (m.Msg == EndComposition) IsComposing = false;
        base.WndProc(ref m);
        if (m.Msg == PaintMessage && Multiline && !Focused && TextLength == 0 && _placeholderText.Length > 0)
        {
            using var graphics = Graphics.FromHwnd(Handle);
            var inset = this.ScalePixels(4);
            var bounds = Rectangle.Inflate(ClientRectangle, -inset, -inset);
            if (bounds.Width > 0 && bounds.Height > 0)
                TextRenderer.DrawText(graphics, _placeholderText, Font, bounds, Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyPrompt(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { IsComposing = false; base.OnLostFocus(e); Invalidate(); }
    private void ApplyPrompt()
    {
        if (IsHandleCreated && !Multiline) SendMessage(Handle, SetCueBanner, IntPtr.Zero, _placeholderText);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr parameter, string text);
}
