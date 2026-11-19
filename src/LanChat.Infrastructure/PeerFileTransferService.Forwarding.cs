using System.Text.Json;
using LanChat.Application;

namespace LanChat.Infrastructure;

public sealed partial class PeerFileTransferService
{
    public async Task<AttachmentDto> PrepareForwardAsync(AttachmentDto file, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
        => (await PrepareForwardCopiesAsync(file, 1, progress, ct).ConfigureAwait(false))[0];

    public async Task<AttachmentDto[]> PrepareForwardCopiesAsync(AttachmentDto file, int copies, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        if (file.Id == Guid.Empty || copies < 1 || copies > 1000) throw new ArgumentException("Lượt chuyển tiếp không hợp lệ.");
        FolderManifest? manifest = null;
        if (file.MimeType == FolderAttachment.MimeType)
        {
            manifest = await ReadFolderManifestAsync(file, ct).ConfigureAwait(false);
            var total = manifest.Files.Sum(item => item.FileSize); long completed = 0;
            foreach (var item in manifest.Files)
            {
                var original = new AttachmentDto(Guid.Empty, file.MessageId, item.RelativePath.Split('/').Last(), "application/octet-stream", item.FileSize, item.Sha256, item.FileKey);
                await DownloadAsync(original, progress is null ? null : new FolderProgress(progress, completed, total), ct).ConfigureAwait(false);
                completed += item.FileSize;
            }
        }
        else await DownloadAsync(file, progress, ct).ConfigureAwait(false);

        // Download and verify each original once. Each destination gets independent IDs and capabilities.
        var result = new List<AttachmentDto>();
        for (var index = 0; index < copies; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (manifest is null)
            {
                result.Add(file with { Id = Guid.NewGuid(), MessageId = Guid.Empty, FileKey = await NewForwardKeyAsync(file.Sha256, ct).ConfigureAwait(false) });
                continue;
            }
            var children = new List<FolderFile>();
            foreach (var item in manifest.Files)
                children.Add(item with { FileKey = await NewForwardKeyAsync(item.Sha256, ct).ConfigureAwait(false) });
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest with { Files = children.ToArray() });
            if (bytes.Length > MaxManifestBytes) throw new InvalidDataException("Danh sách nội dung thư mục quá lớn.");
            var directory = Path.Combine(options.DataDirectory, "Attachments", ".temp"); Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
            try
            {
                await FrameworkIO.WriteBytesAsync(temp, bytes, ct).ConfigureAwait(false);
                result.Add(await ImportSnapshotAsync(temp, file.FileName, FolderAttachment.MimeType, ct).ConfigureAwait(false));
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return result.ToArray();
    }
    private async Task<string> NewForwardKeyAsync(string hash, CancellationToken ct)
    {
        var key = Convert.ToBase64String(FrameworkIO.RandomBytes(DeviceIdentity.KeyBytes));
        await chatStore.RegisterFileKeyAsync(hash.ToLowerInvariant(), key, ct).ConfigureAwait(false);
        return key;
    }
}
