namespace LanChat.WinForms.Controls;

internal static class AttachmentSaveDialog
{
    public static SaveFileDialog Create(string fileName, string? directory = null)
    {
        var name = Path.GetFileName(fileName);
        var extension = Path.GetExtension(name).TrimStart('.');
        return new SaveFileDialog
        {
            Title = "Lưu về máy", FileName = name, InitialDirectory = directory,
            AddExtension = true, SupportMultiDottedExtensions = true, DefaultExt = extension,
            Filter = extension.Length == 0 ? "Tất cả tệp (*.*)|*.*" : $"Tệp {extension.ToUpperInvariant()} (*.{extension})|*.{extension}|Tất cả tệp (*.*)|*.*",
            OverwritePrompt = true, CheckPathExists = true, RestoreDirectory = true
        };
    }
}
