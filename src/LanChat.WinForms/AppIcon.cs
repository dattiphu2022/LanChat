using System.Runtime.InteropServices;
using LanChat.WinForms.Controls;

namespace LanChat.WinForms;

internal static class AppIcon
{
    public static Icon Value { get; } = new(typeof(AppIcon), "Assets.LanChat.ico");
    private static readonly Lazy<Icon> Badged = new(CreateBadgedIcon);

    internal static Icon ForUnread(bool hasUnread) => hasUnread ? Badged.Value : Value;

    private static Icon CreateBadgedIcon()
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawIcon(Value, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            UnreadBadge.PaintDot(graphics, new Rectangle(19, 19, 12, 12), whiteOutline: true);
        }
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
