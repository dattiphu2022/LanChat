using Microsoft.Win32;

namespace LanChat.WinForms;

internal sealed class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LanChat";
    private readonly string _command;

    public WindowsStartup(string dataDirectory)
    {
        var data = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var standard = Path.GetFullPath(new LanChat.Application.NodeOptions().DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        _command = "\"" + System.Windows.Forms.Application.ExecutablePath + "\" --startup";
        if (!string.Equals(data, standard, StringComparison.OrdinalIgnoreCase))
            _command += " --data-dir \"" + data + "\"";
    }

    public bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public bool NeedsUpdate(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var current = key?.GetValue(ValueName) as string;
        return enabled ? !string.Equals(current, _command, StringComparison.OrdinalIgnoreCase) : current is not null;
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled && _command.Length > 260)
            throw new InvalidOperationException("Đường dẫn LanChat quá dài để đăng ký khởi động. Hãy đặt LanChat vào thư mục ngắn hơn.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new InvalidOperationException("Windows không mở được cài đặt khởi động.");
        if (enabled) key.SetValue(ValueName, _command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
