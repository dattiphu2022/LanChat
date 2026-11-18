using System.Text.Json;
using LanChat.Application;

namespace LanChat.Infrastructure;

public sealed partial class PeerFileTransferService
{
    public async Task<AttachmentDto> PrepareForwardAsync(AttachmentDto file, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        if (file.Id == Guid.Empty) throw new ArgumentException("Định danh tệp không hợp lệ.");
        if (file.MimeType == FolderAttachment.MimeType)
        {
            var manifest = await ReadFolderManifestAsync(file, ct).ConfigureAwait(false);
            var total = manifest.Files.Sum(item => item.FileSize);
            var forwardedFiles = new List<FolderFile>();
            long completed = 0;
            foreach (var item in manifest.Files)
            {
                var attachment = new AttachmentDto(Guid.Empty, file.MessageId, item.RelativePath.Split('/').Last(),
                    "application/octet-stream", item.FileSize, item.Sha256, item.FileKey);
                var report = progress is null ? null : new FolderProgress(progress, completed, total);
                var forwarded = await PrepareForwardFileAsync(attachment, report, ct).ConfigureAwait(false);
                forwardedFiles.Add(item with { FileKey = forwarded.FileKey });
                completed += item.FileSize;
                progress?.Report(new(completed, total));
            }

            var folder = manifest with { Files = forwardedFiles.ToArray() };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(folder);
            if (bytes.Length > MaxManifestBytes) throw new InvalidDataException("Danh sách nội dung thư mục quá lớn.");
            var directory = Path.Combine(options.DataDirectory, "Attachments", ".temp");
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
            try
            {
                await FrameworkIO.WriteBytesAsync(temp, bytes, ct).ConfigureAwait(false);
                return await ImportSnapshotAsync(temp, file.FileName, FolderAttachment.MimeType, ct).ConfigureAwait(false);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return await PrepareForwardFileAsync(file, progress, ct).ConfigureAwait(false);
    }

    private async Task<AttachmentDto> PrepareForwardFileAsync(AttachmentDto file, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        // Forward the immutable original. A working copy may contain the user's edits.
        await DownloadAsync(file, progress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // This explicit forward issues a sender-owned key for the verified snapshot.
        var key = Convert.ToBase64String(FrameworkIO.RandomBytes(DeviceIdentity.KeyBytes));
        await chatStore.RegisterFileKeyAsync(file.Sha256.ToLowerInvariant(), key, ct).ConfigureAwait(false);
        return file with { Id = LanChat.Domain.AppIdentity.NewId(), MessageId = Guid.Empty, FileKey = key };
    }
}
