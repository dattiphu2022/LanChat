using System.Runtime.InteropServices;

namespace LanChat.WinForms.Controls;

internal sealed class PromptTextBox : TextBox
{
    private const int SetCueBanner = 0x1501;
    private const int PaintMessage = 0x000F;
    private const int StartComposition = 0x010D;
    private const int EndComposition = 0x010E;
    private string _placeholderText = "";
    public string PlaceholderText
    {
        get => _placeholderText;
        set { _placeholderText = value; ApplyPrompt(); Invalidate(); }
    }

    internal bool IsComposing { get; private set; }
    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
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
