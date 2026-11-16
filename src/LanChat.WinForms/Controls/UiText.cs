using System.Net;
using System.Text.RegularExpressions;

namespace LanChat.WinForms.Controls;

internal static class UiText
{
    private static readonly Regex IpAddresses = new(
        @"(?<![\w.:])(?:\[[a-f0-9:.]*:[a-f0-9:.]*(?:%[\w-]+)?\]|(?:[a-f0-9]{0,4}:){2,}[a-f0-9:.]*(?:%[\w-]+)?|(?:[0-9]{1,3}\.){3}[0-9]{1,3})(?!\w|\.[0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string HideIpAddresses(string? text) => IpAddresses.Replace(text ?? "", match =>
    {
        var value = match.Value.TrimEnd('.');
        var address = value.Trim('[', ']');
        var scope = address.IndexOf('%');
        if (scope >= 0) address = address.Substring(0, scope);
        return IPAddress.TryParse(address, out _) ? "[IP đã ẩn]" + match.Value.Substring(value.Length) : match.Value;
    });
}
