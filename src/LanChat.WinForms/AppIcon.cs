namespace LanChat.WinForms;

internal static class AppIcon
{
    public static Icon Value { get; } = Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath) ?? SystemIcons.Application;
}
