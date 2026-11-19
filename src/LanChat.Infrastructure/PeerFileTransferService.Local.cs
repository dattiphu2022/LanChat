using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using LanChat.Application;

namespace LanChat.Infrastructure;

public sealed partial class PeerFileTransferService
{
    private sealed record LocationRecord(string Path, bool UserSelected, string Hash, string? PendingDestination = null);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _localGates = new();
    internal Func<CancellationToken, Task>? PreparationCheckpoint { get; set; }
    internal bool ForceLocalCopy { get; set; }
    internal Action<string>? LocalSaveCheckpoint { get; set; }

    private SemaphoreSlim LocalGate(AttachmentDto file)
    {
        if (file.Id == Guid.Empty) throw new ArgumentException("Định danh tệp không hợp lệ.");
        _ = BlobPath(file.Sha256);
        return _localGates.GetOrAdd(file.Id, _ => new SemaphoreSlim(1, 1));
    }
    private string LocationPath(AttachmentDto file) => Path.Combine(options.DataDirectory, "Attachments", "Locations", file.Id.ToString("N") + ".json");
    private static bool Exists(AttachmentDto file, string path) => file.MimeType == FolderAttachment.MimeType ? Directory.Exists(path) : File.Exists(path);
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private LocationRecord? ReadLocation(AttachmentDto file)
    {
        var path = LocationPath(file);
        if (!File.Exists(path)) return null;
        try
        {
            var record = JsonSerializer.Deserialize<LocationRecord>(File.ReadAllText(path));
            if (record is null || !Path.IsPathRooted(record.Path) || !string.Equals(record.Hash, file.Sha256, StringComparison.OrdinalIgnoreCase) ||
                (record.PendingDestination is not null && !Path.IsPathRooted(record.PendingDestination)))
                throw new InvalidDataException("Thông tin vị trí tệp không hợp lệ.");
            return record;
        }
        catch (JsonException ex) { throw new IOException("Không đọc được vị trí tệp đã lưu. Vui lòng kiểm tra thư mục dữ liệu LanChat.", ex); }
    }
    private async Task WriteLocationAsync(AttachmentDto file, LocationRecord record, CancellationToken ct)
    {
        var target = LocationPath(file); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await FrameworkIO.WriteTextAsync(temporary, JsonSerializer.Serialize(record), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); FrameworkIO.PublishFile(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<LocationRecord?> RecoverLocationAsync(AttachmentDto file, CancellationToken ct)
    {
        var record = ReadLocation(file);
        if (record?.PendingDestination is not { } destination) return record;
        if (Exists(file, record.Path)) record = record with { PendingDestination = null };
        else if (Exists(file, destination)) record = new(destination, true, file.Sha256);
        else return record; // Keep both locations available for the user's recovery dialog.
        await WriteLocationAsync(file, record, ct).ConfigureAwait(false);
        return record;
    }
    private IEnumerable<string> RecoveryPaths(AttachmentDto file)
    {
        var paths = new List<string> { OpenPath(file) };
        try
        {
            var record = ReadLocation(file);
            if (record is not null) { paths.Add(record.Path); if (record.PendingDestination is not null) paths.Add(record.PendingDestination); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return paths;
    }

    public Task<LocalAttachmentLocation> GetLocalLocationAsync(AttachmentDto file, CancellationToken ct = default)
    {
        // Location files are published atomically. A read must never queue behind a download.
        ct.ThrowIfCancellationRequested();
        if (file.Id == Guid.Empty) throw new ArgumentException("Định danh tệp không hợp lệ.");
        _ = BlobPath(file.Sha256);
        var record = ReadLocation(file);
        var path = record?.Path ?? OpenPath(file);
        if (record?.PendingDestination is { } destination && !Exists(file, path) && Exists(file, destination)) path = destination;
        return Task.FromResult(new LocalAttachmentLocation(path, record?.UserSelected == true || record?.PendingDestination is not null, Exists(file, path)));
    }
    public Task<string> PrepareOpenAsync(AttachmentDto file, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
        => JoinPreparationAsync(file, progress, ct);
    private async Task<string> PrepareExclusiveAsync(AttachmentDto file, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var gate = LocalGate(file);
        progress?.Report(new(0, file.FileSize, AttachmentPhase.Waiting));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (PreparationCheckpoint is { } checkpoint) await checkpoint(ct).ConfigureAwait(false);
            return await PrepareLocalCoreAsync(file, progress, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); logger.LogDebug("Attachment {Attachment} preparation ended after {Milliseconds} ms", file.Id, started.ElapsedMilliseconds); }
    }
    private async Task<string> PrepareLocalCoreAsync(AttachmentDto file, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var record = await RecoverLocationAsync(file, ct).ConfigureAwait(false);
        if (record is not null)
        {
            if (Exists(file, record.Path)) return record.Path;
            if (record.UserSelected || record.PendingDestination is not null)
                throw new LocalAttachmentMissingException(record.PendingDestination ?? record.Path);
        }
        return await PrepareManagedAsync(file, progress, ct).ConfigureAwait(false);
    }

    public async Task RelinkLocalAsync(AttachmentDto file, string path, CancellationToken ct = default)
    {
        var gate = LocalGate(file); await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            path = Path.GetFullPath(path); ValidateUserDestination(path, OpenPath(file), file);
            if (!Exists(file, path)) throw new IOException("Không tìm thấy tệp hoặc thư mục đã chọn.");
            await WriteLocationAsync(file, new(path, !SamePath(path, OpenPath(file)), file.Sha256), ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async Task<string> RestoreLocalAsync(AttachmentDto file, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        var gate = LocalGate(file); await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var record = await RecoverLocationAsync(file, ct).ConfigureAwait(false);
            if (record?.UserSelected == true && Exists(file, record.Path))
                throw new IOException("Bản đã lưu vẫn còn. Hãy mở bản đó để giữ các chỉnh sửa.");
            var path = await PrepareManagedAsync(file, progress, ct, force: true).ConfigureAwait(false);
            await WriteLocationAsync(file, new(path, false, file.Sha256), ct).ConfigureAwait(false);
            return path;
        }
        finally { gate.Release(); }
    }

    public async Task<string> SaveLocalAsync(AttachmentDto file, string destination, bool overwrite = false,
        IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        // Join any background preparation before acquiring the writer gate for the save.
        await PrepareOpenAsync(file, progress, ct).ConfigureAwait(false);
        progress?.Report(new(0, file.FileSize, AttachmentPhase.Saving));
        var gate = LocalGate(file); await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var source = await PrepareLocalCoreAsync(file, progress, ct).ConfigureAwait(false);
            destination = Path.GetFullPath(destination);
            if (SamePath(source, destination)) return source;
            ValidateUserDestination(destination, source, file);
            var folder = file.MimeType == FolderAttachment.MimeType;
            if (Directory.Exists(destination) || (!overwrite && File.Exists(destination))) throw new IOException("Đích đã tồn tại. Hãy chọn tên khác.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var record = await RecoverLocationAsync(file, ct).ConfigureAwait(false) ?? new LocationRecord(source, false, file.Sha256);
            var managed = !record.UserSelected && SamePath(source, OpenPath(file));
            if (managed) RejectFolderPathLinks(source, options.DataDirectory);
            CheckWorkingCopy(source, folder);
            ct.ThrowIfCancellationRequested();
            if (managed && !ForceLocalCopy && SamePath(Path.GetPathRoot(source)!, Path.GetPathRoot(destination)!))
            {
                await WriteLocationAsync(file, record with { PendingDestination = destination }, ct).ConfigureAwait(false);
                LocalSaveCheckpoint?.Invoke("intent");
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (folder) Directory.Move(source, destination);
                    else if (overwrite && File.Exists(destination)) File.Replace(source, destination, null);
                    else File.Move(source, destination);
                    LocalSaveCheckpoint?.Invoke("published");
                    // Once moved, finish recording the actual location even if the caller canceled.
                    await WriteLocationAsync(file, new(destination, true, file.Sha256), CancellationToken.None).ConfigureAwait(false);
                    LocalSaveCheckpoint?.Invoke("mapped");
                    return destination;
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 17 && Exists(file, source))
                { await WriteLocationAsync(file, record, ct).ConfigureAwait(false); }
            }
            var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".lanchat-" + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                await CopyWorkingAsync(source, temporary, folder, progress, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (folder) Directory.Move(temporary, destination);
                else if (overwrite && File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
                LocalSaveCheckpoint?.Invoke("published");
                await WriteLocationAsync(file, new(destination, true, file.Sha256), CancellationToken.None).ConfigureAwait(false);
                LocalSaveCheckpoint?.Invoke("mapped");
                if (managed)
                {
                    // Only our exact per-attachment working copy is eligible for cleanup.
                    try { RemoveManagedCopy(source, file); }
                    catch (IOException) { /* The saved location is committed; an internal duplicate is harmless. */ }
                    catch (UnauthorizedAccessException) { }
                }
                return destination;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (Directory.Exists(temporary))
                {
                    if (!SamePath(Path.GetDirectoryName(temporary)!, Path.GetDirectoryName(destination)!) || !Path.GetFileName(temporary).StartsWith(".lanchat-", StringComparison.Ordinal))
                        throw new IOException("Đường dẫn tạm không hợp lệ.");
                    Directory.Delete(temporary, true);
                }
            }
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        { throw new IOException("Tệp đang được ứng dụng khác sử dụng. Hãy đóng tệp rồi thử lại.", ex); }
        finally { gate.Release(); }
    }
    private void ValidateUserDestination(string destination, string source, AttachmentDto file)
    {
        if (SamePath(destination, source)) return;
        var data = Path.GetFullPath(options.DataDirectory).TrimEnd('\\', '/');
        if (IsWithin(destination, data)) throw new IOException("Hãy chọn nơi lưu bên ngoài thư mục dữ liệu LanChat.");
        if (file.MimeType == FolderAttachment.MimeType && (IsWithin(destination, source.TrimEnd('\\', '/')) || IsWithin(source, destination.TrimEnd('\\', '/'))))
            throw new IOException("Không thể lưu thư mục vào chính nó, thư mục con hoặc thư mục cha của nó.");
    }
    private void RemoveManagedCopy(string source, AttachmentDto file)
    {
        if (!SamePath(source, OpenPath(file)) || !IsWithin(Path.GetFullPath(source), Path.GetFullPath(Path.Combine(options.DataDirectory, "Attachments", "Open"))))
            throw new IOException("Không được dọn tệp ngoài thư mục làm việc LanChat.");
        RejectFolderPathLinks(source, options.DataDirectory);
        if (file.MimeType == FolderAttachment.MimeType) Directory.Delete(source, true); else File.Delete(source);
    }
    private static (string[] Directories, string[] Files) WorkingTree(string source)
    {
        var directories = new List<string>(); var files = new List<string>(); var pending = new Stack<string>(); pending.Push(source);
        while (pending.Count > 0)
        {
            var directory = pending.Pop(); RejectFolderLink(directory);
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                RejectFolderLink(entry.FullName);
                if (entry is DirectoryInfo) { directories.Add(entry.FullName); pending.Push(entry.FullName); }
                else files.Add(entry.FullName);
            }
        }
        return (directories.ToArray(), files.ToArray());
    }
    private static void CheckWorkingCopy(string source, bool folder)
    {
        RejectFolderLink(source);
        var files = folder ? WorkingTree(source).Files : new[] { source };
        foreach (var file in files) { using var check = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None); }
    }
    private static async Task CopyWorkingAsync(string source, string target, bool folder, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var tree = folder ? WorkingTree(source) : (Directories: Array.Empty<string>(), Files: new[] { source });
        var total = tree.Files.Sum(file => new FileInfo(file).Length); long completed = 0;
        if (folder)
        {
            Directory.CreateDirectory(target);
            foreach (var directory in tree.Directories) Directory.CreateDirectory(Path.Combine(target, directory.Substring(source.TrimEnd('\\').Length + 1)));
        }
        foreach (var file in tree.Files)
        {
            ct.ThrowIfCancellationRequested();
            var destination = folder ? Path.Combine(target, file.Substring(source.TrimEnd('\\').Length + 1)) : target;
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer, 0, count, ct).ConfigureAwait(false); completed += count;
                    progress?.Report(new(completed, total, AttachmentPhase.Saving));
                }
            }
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(file));
        }
        progress?.Report(new(completed, total, AttachmentPhase.Saving));
    }
}
