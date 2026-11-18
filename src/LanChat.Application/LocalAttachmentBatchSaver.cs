namespace LanChat.Application;

public sealed record AttachmentSaveResult(AttachmentDto File, string? Path, string? Error, bool Canceled = false);
public sealed record AttachmentSaveProgress(int Index, int Count, string FileName, FileProgress? Bytes = null);

/// <summary>One message per batch; successful items remain committed when another item fails.</summary>
public sealed class LocalAttachmentBatchSaver(IFileTransferService files)
{
    public static string Destination(string directory, string name, bool folder, string? currentPath = null)
    {
        directory = Path.GetFullPath(directory);
        name = Path.GetFileName(name.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("Tên đính kèm không hợp lệ.");
        var target = Path.Combine(directory, name);
        bool Same(string candidate) => currentPath is not null && string.Equals(Path.GetFullPath(candidate).TrimEnd('\\', '/'),
            Path.GetFullPath(currentPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        var stem = folder ? name : Path.GetFileNameWithoutExtension(name);
        var extension = folder ? "" : Path.GetExtension(name);
        for (var index = 2; !Same(target) && (File.Exists(target) || Directory.Exists(target)); index++)
            target = Path.Combine(directory, $"{stem} ({index}){extension}");
        return target;
    }

    public async Task<AttachmentSaveResult[]> SaveAsync(AttachmentDto[] attachments, string directory,
        IProgress<AttachmentSaveProgress>? progress = null, CancellationToken ct = default)
    {
        if (attachments.Select(f => f.MessageId).Distinct().Count() > 1)
            throw new ArgumentException("Chỉ lưu đính kèm của một tin nhắn trong mỗi lượt.");
        var results = new List<AttachmentSaveResult>();
        for (var i = 0; i < attachments.Length; i++)
        {
            var file = attachments[i];
            if (ct.IsCancellationRequested) { results.Add(new(file, null, null, true)); continue; }
            var index = i + 1;
            progress?.Report(new(index, attachments.Length, file.FileName));
            try
            {
                var location = await files.GetLocalLocationAsync(file, ct).ConfigureAwait(false);
                var target = Destination(directory, Path.GetFileName(location.Path), file.MimeType == FolderAttachment.MimeType, location.Path);
                var saved = await files.SaveLocalAsync(file, target, progress: new InlineProgress<FileProgress>(bytes =>
                    progress?.Report(new(index, attachments.Length, file.FileName, bytes))), ct: ct).ConfigureAwait(false);
                results.Add(new(file, saved, null));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { results.Add(new(file, null, null, true)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { results.Add(new(file, null, ex.Message)); }
        }
        return results.ToArray();
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    { public void Report(T value) => action(value); }
}
