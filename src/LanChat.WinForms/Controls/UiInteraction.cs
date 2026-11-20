using System.Runtime.InteropServices;

namespace LanChat.WinForms.Controls;

internal static class UiInteraction
{
    public static Form? Owner(Control source)
    {
        var root = source.TopLevelControl as Form ?? source.FindForm();
        return root is null ? null : FindOwnedDialog(root, forOwnership: true) ?? root;
    }

    public static Form? ForegroundDialog(Form root) => FindOwnedDialog(root, forOwnership: false);

    private static Form? FindOwnedDialog(Form root, bool forOwnership)
    {
        foreach (var child in root.OwnedForms.Reverse())
            if (child is not Forms.TransferCenterForm && child.Visible && !child.IsDisposed && (!forOwnership || child is not Forms.AttachmentOpenProgressForm))
                return FindOwnedDialog(child, forOwnership) ?? child;
        return null;
    }

    public static DialogResult ShowDialog(Form dialog, Control source)
    {
        var previous = source.FindForm()?.ActiveControl;
        try { return dialog.ShowDialog(Owner(source)); }
        finally { if (!source.IsDisposed && previous is { IsDisposed: false, CanFocus: true }) previous.Focus(); }
    }

    public static void Warn(Control source, string message, string title = "LanChat")
    {
        if (!source.IsDisposed && !source.Disposing)
            MessageBox.Show(Owner(source), UiText.HideIpAddresses(message), title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    public static bool Discard(Control source, string message, string title = "Thay đổi chưa lưu") =>
        MessageBox.Show(Owner(source), message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public static bool Copy(Control source, string text)
    {
        if (text.Length == 0) return false;
        try { Clipboard.SetText(text); return true; }
        catch (ExternalException) { Warn(source, "Bảng nhớ tạm đang bận. Vui lòng thử sao chép lại.", "Chưa sao chép được"); return false; }
    }

    public static Button CloseButton(Form form, string caption = "Đóng")
    {
        var button = Theme.Secondary(caption, UiIcon.Close);
        button.DialogResult = DialogResult.Cancel;
        form.CancelButton = button;
        form.ShowInTaskbar = false;
        form.MinimizeBox = false;
        return button;
    }

    public static void Wrap(Label label, Control container)
    {
        label.AutoSize = true; label.AutoEllipsis = false; label.UseMnemonic = false;
        void Fit() => label.MaximumSize = new Size(Math.Max(1,
            container.ClientSize.Width - container.Padding.Horizontal - label.Margin.Horizontal), 0);
        container.SizeChanged += (_, _) => Fit();
        Fit();
    }

    public static async Task SaveCopyAsync(string source, string destination, CancellationToken ct)
    {
        var temporary = TemporaryPath(destination);
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, 65536, ct);
            ct.ThrowIfCancellationRequested();
            Publish(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void SaveFile(string destination, Action<Stream> write)
    {
        var temporary = TemporaryPath(destination);
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(output);
            Publish(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string TemporaryPath(string destination) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!,
        ".lanchat-" + Guid.NewGuid().ToString("N") + ".tmp");
    private static void Publish(string temporary, string destination)
    {
        if (File.Exists(destination)) File.Replace(temporary, destination, null);
        else File.Move(temporary, destination);
    }
}
