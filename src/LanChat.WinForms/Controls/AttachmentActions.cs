using System.ComponentModel;
using System.Diagnostics;
using LanChat.Application;
using LanChat.WinForms.Forms;

namespace LanChat.WinForms.Controls;

internal enum AttachmentCommand { Open, Save, SaveMessage, SaveMessageImages }

/// <summary>Shared by chat, the information panel and viewers within one network.</summary>
internal sealed class AttachmentActions(IFileTransferService files)
{
    private readonly HashSet<Guid> _busy = [];
    public event Action<Guid>? Changed;
    public bool IsBusy(Guid id) => _busy.Contains(id);
    public Task<LocalAttachmentLocation> LocationAsync(AttachmentDto file, CancellationToken ct) =>
        Task.Run(() => files.GetLocalLocationAsync(file, ct), ct);

    public async Task OpenAsync(Control owner, AttachmentDto file, CancellationToken lifetime, Action<string>? launch = null)
    {
        if (!_busy.Add(file.Id)) return;
        Changed?.Invoke(file.Id);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        using var progress = new AttachmentOpenProgressForm(file.FileName, cancellation, file.MimeType == FolderAttachment.MimeType);
        try
        {
            var report = new Progress<FileProgress>(p => { if (!progress.IsDisposed && !cancellation.IsCancellationRequested) progress.Report(p); });
            var prepare = Task.Run(() => files.PrepareOpenAsync(file, report, cancellation.Token));
            if (await Task.WhenAny(prepare, Task.Delay(350, cancellation.Token)) != prepare && !cancellation.IsCancellationRequested)
                progress.Show(UiInteraction.Owner(owner));
            string path;
            try { path = await prepare; }
            catch (LocalAttachmentMissingException missing)
            {
                progress.Hide();
                path = await RecoverAsync(owner, file, missing, cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (owner.IsDisposed) return;
            if (launch is not null) launch(path);
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open", WorkingDirectory = Path.GetDirectoryName(path) });
        }
        catch (OperationCanceledException) { }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 1155 or 31)
        { progress.Hide(); UiInteraction.Warn(owner, "Windows chưa có ứng dụng mặc định để mở loại tệp này. Hãy chọn ứng dụng phù hợp rồi bấm Mở lại."); }
        catch (Exception ex) { progress.Hide(); UiInteraction.Warn(owner, ex.Message, "Chưa mở được đính kèm"); }
        finally { progress.Close(); _busy.Remove(file.Id); Changed?.Invoke(file.Id); }
    }

    public async Task SaveAsync(Control owner, AttachmentDto file, CancellationToken lifetime)
    {
        if (!_busy.Add(file.Id)) return;
        Changed?.Invoke(file.Id);
        try
        {
            var location = await LocationAsync(file, lifetime);
            if (location.UserSelected && !location.Exists)
            {
                await RecoverAsync(owner, file, new LocalAttachmentMissingException(location.Path), lifetime);
                location = await LocationAsync(file, lifetime);
            }
            lifetime.ThrowIfCancellationRequested(); if (owner.IsDisposed) return;
            string target;
            var folder = file.MimeType == FolderAttachment.MimeType;
            if (folder)
            {
                using var dialog = new FolderPickerDialog { Title = "Chọn nơi lưu thư mục" };
                if (dialog.ShowDialog(UiInteraction.Owner(owner)) != DialogResult.OK || dialog.SelectedPath is null) return;
                target = LocalAttachmentBatchSaver.Destination(dialog.SelectedPath, Path.GetFileName(location.Path), true, location.Path);
            }
            else
            {
                using var dialog = AttachmentSaveDialog.Create(Path.GetFileName(location.Path),
                    location.UserSelected ? Path.GetDirectoryName(location.Path) : null);
                if (dialog.ShowDialog(UiInteraction.Owner(owner)) != DialogResult.OK) return;
                target = dialog.FileName;
            }
            using var form = new AttachmentSaveForm(file.FileName, async (progress, ct) =>
            {
                try
                {
                    var saved = await files.SaveLocalAsync(file, target, !folder,
                        new InlineProgress<FileProgress>(p => progress.Report(new(1, 1, file.FileName, p))), ct).ConfigureAwait(false);
                    return new[] { new AttachmentSaveResult(file, saved, null) };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return [new(file, null, null, true)]; }
                catch (Exception ex) { return [new(file, null, ex.Message)]; }
            }, lifetime);
            UiInteraction.ShowDialog(form, owner);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { UiInteraction.Warn(owner, ex.Message, "Chưa lưu được đính kèm"); }
        finally { _busy.Remove(file.Id); Changed?.Invoke(file.Id); }
    }

    public async Task SaveBatchAsync(Control owner, AttachmentDto[] attachments, CancellationToken lifetime)
    {
        if (attachments.Length == 0 || attachments.Any(f => _busy.Contains(f.Id))) return;
        foreach (var file in attachments) { _busy.Add(file.Id); Changed?.Invoke(file.Id); }
        try
        {
            // Resolve missing working copies explicitly before starting the batch.
            foreach (var file in attachments)
            {
                LocalAttachmentLocation location;
                try { location = await LocationAsync(file, lifetime); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; } // Report this item in the batch results.
                if (location.UserSelected && !location.Exists) await RecoverAsync(owner, file, new LocalAttachmentMissingException(location.Path), lifetime);
            }
            lifetime.ThrowIfCancellationRequested(); if (owner.IsDisposed) return;
            using var dialog = new FolderPickerDialog { Title = "Chọn nơi lưu các đính kèm của tin nhắn" };
            if (dialog.ShowDialog(UiInteraction.Owner(owner)) != DialogResult.OK || dialog.SelectedPath is null) return;
            using var form = new AttachmentSaveForm($"{attachments.Length} đính kèm của tin nhắn", (progress, ct) =>
                new LocalAttachmentBatchSaver(files).SaveAsync(attachments, dialog.SelectedPath, progress, ct), lifetime);
            UiInteraction.ShowDialog(form, owner);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { UiInteraction.Warn(owner, ex.Message, "Chưa lưu được đính kèm"); }
        finally { foreach (var file in attachments) { _busy.Remove(file.Id); Changed?.Invoke(file.Id); } }
    }

    private async Task<string> RecoverAsync(Control owner, AttachmentDto file, LocalAttachmentMissingException missing, CancellationToken ct)
    {
        if (owner.IsDisposed) throw new OperationCanceledException(ct);
        using var recovery = new AttachmentRecoveryForm(missing.SavedPath);
        var choice = UiInteraction.ShowDialog(recovery, owner);
        ct.ThrowIfCancellationRequested();
        if (choice == DialogResult.Retry)
        {
            string? path;
            if (file.MimeType == FolderAttachment.MimeType)
            {
                using var picker = new FolderPickerDialog { Title = "Tìm lại thư mục đang làm việc" };
                path = picker.ShowDialog(UiInteraction.Owner(owner)) == DialogResult.OK ? picker.SelectedPath : null;
            }
            else
            {
                using var picker = new OpenFileDialog { Title = "Tìm lại tệp đang làm việc", FileName = Path.GetFileName(missing.SavedPath), CheckFileExists = true };
                path = picker.ShowDialog(UiInteraction.Owner(owner)) == DialogResult.OK ? picker.FileName : null;
            }
            if (path is not null) { await Task.Run(() => files.RelinkLocalAsync(file, path, ct), ct); return path; }
        }
        else if (choice == DialogResult.Yes)
        {
            string? restored = null;
            using var form = new AttachmentSaveForm("Tạo lại " + file.FileName, async (progress, token) =>
            {
                restored = await files.RestoreLocalAsync(file, new InlineProgress<FileProgress>(p => progress.Report(new(1, 1, file.FileName, p))), token).ConfigureAwait(false);
                return new[] { new AttachmentSaveResult(file, restored, null) };
            }, ct);
            UiInteraction.ShowDialog(form, owner);
            if (restored is not null) return restored;
        }
        throw new OperationCanceledException("Đã hủy tìm lại đính kèm.");
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
