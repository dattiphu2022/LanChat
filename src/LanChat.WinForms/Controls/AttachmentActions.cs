using System.ComponentModel;
using System.Diagnostics;
using LanChat.Application;
using LanChat.Infrastructure;
using LanChat.WinForms.Forms;

namespace LanChat.WinForms.Controls;

internal enum AttachmentCommand { Open, Save, SaveMessage, SaveMessageImages }

/// <summary>Shared by chat, the information panel and viewers within one network.</summary>
internal sealed class AttachmentActions : IDisposable
{
    private readonly IFileTransferService files;
    private readonly System.Windows.Forms.Timer _watchTimer = new() { Interval = 500 };
    private readonly Dictionary<Guid, int> _watched = [];
    private readonly Dictionary<Guid, FileProgress?> _snapshots = [];
    private bool _disposed;
    private int _idleTicks;
    internal event Action? PollIdle;
    public AttachmentActions(IFileTransferService files)
    {
        this.files = files;
        _watchTimer.Tick += (_, _) =>
        {
            foreach (var id in _watched.Keys.ToArray())
            {
                var snapshot = files.GetPreparationState(id);
                _snapshots.TryGetValue(id, out var previous);
                if (snapshot == previous) continue;
                _snapshots[id] = snapshot; Notify(id);
            }
            if (++_idleTicks % 4 == 0) PollIdle?.Invoke();
        };
    }
    internal void SetResourceSaving(bool enabled) => _watchTimer.Interval = enabled ? 1000 : 500;

    internal void Watch(Guid id)
    {
        if (_disposed) return;
        _watched.TryGetValue(id, out var count); _watched[id] = count + 1; _watchTimer.Start();
    }
    internal void Unwatch(Guid id)
    {
        if (_disposed || !_watched.TryGetValue(id, out var count)) return;
        if (count > 1) _watched[id] = count - 1;
        else { _watched.Remove(id); _snapshots.Remove(id); }
        if (_watched.Count == 0) _watchTimer.Stop();
    }
    internal string? BackgroundState(Guid id) => files.GetPreparationState(id) is { } progress ? AttachmentProgressText.Describe(progress) : null;
    public void Dispose() { _disposed = true; _watchTimer.Stop(); _watchTimer.Dispose(); _watched.Clear(); _snapshots.Clear(); }

    private readonly HashSet<Guid> _busy = [];
    public event Action<Guid>? Changed;
    private readonly Dictionary<Guid, string> _states = [];
    private readonly Dictionary<Guid, string> _errors = [];
    internal bool HasActiveOperations => _busy.Count > 0;
    internal FileProgress? Progress(Guid id) => files.GetPreparationState(id);
    internal string? Failure(Guid id) => _errors.TryGetValue(id, out var error) ? error : null;
    internal async Task PrepareAsync(AttachmentDto file, CancellationToken networkLifetime)
    {
        if (!_busy.Add(file.Id)) return;
        _errors.Remove(file.Id); _states[file.Id] = "Đang chuẩn bị…"; Notify(file.Id);
        try
        {
            var progress = new Progress<FileProgress>(p => { if (!_disposed) Report(file.Id, p); });
            await Task.Run(() => files.PrepareOpenAsync(file, progress, networkLifetime), networkLifetime);
        }
        catch (OperationCanceledException) when (networkLifetime.IsCancellationRequested) { }
        catch (Exception) { if (!_disposed) _errors[file.Id] = "Chuẩn bị thất bại · Có thể thử lại"; }
        finally { _busy.Remove(file.Id); _states.Remove(file.Id); if (!_disposed) Notify(file.Id); }
    }
    public bool IsBusy(Guid id) => _busy.Contains(id);
    public string State(Guid id) => _states.TryGetValue(id, out var state) ? state : "Đang chờ tác vụ…";
    private void Notify(Guid id)
    {
        if (Changed is null) return;
        foreach (Action<Guid> listener in Changed.GetInvocationList())
            try { listener(id); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { } catch (Exception ex) { Debug.WriteLine(ex.GetType().Name); }
    }
    private void Report(Guid id, FileProgress value)
    {
        var state = AttachmentProgressText.Describe(value);
        if (_states.TryGetValue(id, out var old) && old == state) return;
        _states[id] = state; Notify(id);
    }
    internal Task<(long TotalBytes, int FileCount)?> FolderSummaryAsync(AttachmentDto file, CancellationToken ct) =>
        files is PeerFileTransferService peerFiles
            ? Task.Run(() => peerFiles.ReadLocalFolderSummaryAsync(file, ct), ct)
            : Task.FromResult<(long, int)?>(null);

    public Task<LocalAttachmentLocation> LocationAsync(AttachmentDto file, CancellationToken ct) =>
        Task.Run(() => files.GetLocalLocationAsync(file, ct), ct);

    public async Task OpenAsync(Control owner, AttachmentDto file, CancellationToken lifetime, Action<string>? launch = null)
    {
        if (!_busy.Add(file.Id)) return;
        Notify(file.Id);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        using var progress = new AttachmentOpenProgressForm(file.FileName, cancellation, file.MimeType == FolderAttachment.MimeType);
        try
        {
            var report = new Progress<FileProgress>(p => { if (!progress.IsDisposed && !cancellation.IsCancellationRequested) { progress.Report(p); Report(file.Id, p); } });
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
            else
            {
                var launching = new FileProgress(0, 0, AttachmentPhase.Launching);
                progress.Report(launching); Report(file.Id, launching);
                await LaunchAsync(path);
            }
        }
        catch (OperationCanceledException) { }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 1155 or 31)
        { progress.Hide(); UiInteraction.Warn(owner, "Windows chưa có ứng dụng mặc định để mở loại tệp này. Hãy chọn ứng dụng phù hợp rồi bấm Mở lại."); }
        catch (Exception ex) { progress.Hide(); UiInteraction.Warn(owner, ex.Message, "Chưa mở được đính kèm"); }
        finally { progress.Close(); _busy.Remove(file.Id); _states.Remove(file.Id); Notify(file.Id); }
    }

    private async Task<LocalAttachmentLocation> ReadLocationWithProgressAsync(Control owner, AttachmentDto file, CancellationToken lifetime)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        using var form = new AttachmentOpenProgressForm(file.FileName, cancellation);
        form.Report(new(0, file.FileSize, AttachmentPhase.Waiting));
        try
        {
            var read = LocationAsync(file, cancellation.Token);
            if (await Task.WhenAny(read, Task.Delay(350, cancellation.Token)) != read && !cancellation.IsCancellationRequested && !owner.IsDisposed)
                form.Show(UiInteraction.Owner(owner));
            var location = await read;
            cancellation.Token.ThrowIfCancellationRequested();
            return location;
        }
        finally { form.Close(); }
    }

    public async Task SaveAsync(Control owner, AttachmentDto file, CancellationToken lifetime)
    {
        if (!_busy.Add(file.Id)) return;
        Notify(file.Id);
        try
        {
            var location = await ReadLocationWithProgressAsync(owner, file, lifetime);
            if (location.UserSelected && !location.Exists)
            {
                await RecoverAsync(owner, file, new LocalAttachmentMissingException(location.Path), lifetime);
                location = await ReadLocationWithProgressAsync(owner, file, lifetime);
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
        finally { _busy.Remove(file.Id); _states.Remove(file.Id); Notify(file.Id); }
    }

    public async Task SaveBatchAsync(Control owner, AttachmentDto[] attachments, CancellationToken lifetime)
    {
        if (attachments.Length == 0 || attachments.Any(f => _busy.Contains(f.Id))) return;
        foreach (var file in attachments) { _busy.Add(file.Id); Notify(file.Id); }
        try
        {
            // Resolve missing working copies explicitly before starting the batch.
            foreach (var file in attachments)
            {
                LocalAttachmentLocation location;
                try { location = await ReadLocationWithProgressAsync(owner, file, lifetime); }
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
        finally { foreach (var file in attachments) { _busy.Remove(file.Id); _states.Remove(file.Id); Notify(file.Id); } }
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

    private static Task LaunchAsync(string path)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open", WorkingDirectory = Path.GetDirectoryName(path) }); result.TrySetResult(true); }
            catch (Exception ex) { result.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
