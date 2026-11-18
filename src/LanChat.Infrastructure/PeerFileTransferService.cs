using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using LanChat.Application;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

public sealed partial class PeerFileTransferService(NodeOptions options, IDistributedStore store, ChatStore chatStore,
    WireProtocol wire, ClusterTls tls, FaultInjection faults, ILogger<PeerFileTransferService> logger) : IFileTransferService
{
    private string BlobPath(string hash)
    {
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("SHA256 không hợp lệ.");
        hash = hash.ToLowerInvariant();
        return Path.Combine(options.DataDirectory, "Attachments", hash.Substring(0, 2), hash + ".bin");
    }
    public async Task<string?> GetLocalPathAsync(string sha256, CancellationToken ct = default)
    {
        using var stream = await OpenVerifiedLocalAsync(sha256, ct);
        return stream is null ? null : BlobPath(sha256);
    }
    public async Task<AttachmentDto> ImportLocalAsync(string path, CancellationToken ct = default)
        => await ImportSnapshotAsync(path, null, null, ct);

    private async Task<AttachmentDto> ImportSnapshotAsync(string path, string? fileName, string? contentType, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 20L * 1024 * 1024 * 1024) throw new ArgumentException("File không tồn tại hoặc vượt quá 20 GB.");
        var tempRoot = Path.Combine(options.DataDirectory, "Attachments", ".temp"); Directory.CreateDirectory(tempRoot);
        var temp = Path.Combine(tempRoot, LanChat.Domain.AppIdentity.NewId() + ".part");
        try
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await source.CopyToAsync(destination, ct);
            string hash;
            using (var file = File.OpenRead(temp)) hash = FrameworkIO.Hex(await FrameworkIO.HashAsync(file, ct)).ToLowerInvariant();
            var target = BlobPath(hash); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Replace atomically with the verified copy, including recovery from a damaged local blob.
            await PublishSnapshotAsync(temp, target, hash, ct);
            // Remember only a recovery candidate. It must still match the original hash before reuse.
            if (contentType != FolderAttachment.MimeType)
            {
                var reference = target + ".source.json";
                var referenceTemp = reference + "." + LanChat.Domain.AppIdentity.NewId() + ".part";
                try
                {
                    await FrameworkIO.WriteTextAsync(referenceTemp, JsonSerializer.Serialize(Path.GetFullPath(path)), ct);
                    FrameworkIO.PublishFile(referenceTemp, reference);
                }
                finally { if (File.Exists(referenceTemp)) File.Delete(referenceTemp); }
            }
            var mime = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff", ".pdf" => "application/pdf", ".txt" => "text/plain", _ => "application/octet-stream" };
            var fileKey = Convert.ToBase64String(FrameworkIO.RandomBytes(DeviceIdentity.KeyBytes));
            await chatStore.RegisterFileKeyAsync(hash, fileKey, ct);
            logger.LogInformation("Imported local attachment {Bytes} bytes", info.Length);
            return new(LanChat.Domain.AppIdentity.NewId(), Guid.Empty, fileName ?? info.Name, contentType ?? mime, new FileInfo(target).Length, hash, fileKey);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task<FileAvailability> HasFileAsync(string hash, CancellationToken ct)
    {
        using var stream = await OpenVerifiedLocalAsync(hash, ct);
        return new(stream is not null, stream?.Length ?? 0);
    }

    // Keep this same read handle open through the transfer, so another app cannot change
    // the bytes between verification and sending them to a peer.
    public async Task<FileStream?> OpenVerifiedLocalAsync(string hash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var target = BlobPath(hash);
        var stream = await OpenMatchingAsync(target, hash, ct);
        if (stream is not null) return stream;
        var candidates = new List<string>();
        try
        {
            var reference = target + ".source.json";
            if (File.Exists(reference) && JsonSerializer.Deserialize<string>(await FrameworkIO.ReadTextAsync(reference, ct)) is { } source)
                candidates.Add(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        // Also recover older attachments without a source reference from an unchanged named copy.
        foreach (var file in (await store.GetAttachmentsAsync(ct: ct)).Where(x => x.MimeType != FolderAttachment.MimeType &&
            string.Equals(x.Sha256, hash, StringComparison.OrdinalIgnoreCase)))
            candidates.Add(OpenPath(file));
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var temp = target + "." + LanChat.Domain.AppIdentity.NewId() + ".part";
            try
            {
                if (string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue;
                using var source = await OpenMatchingAsync(candidate, hash, ct);
                if (source is null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    await source.CopyToAsync(output, ct);
                ct.ThrowIfCancellationRequested();
                await PublishSnapshotAsync(temp, target, hash, ct);
                stream = await OpenMatchingAsync(target, hash, ct);
                if (stream is not null) return stream;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { logger.LogWarning("Attachment recovery candidate unavailable: {Type}", ex.GetType().Name); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        // Another request may have repaired the blob while this one was examining candidates.
        return await OpenMatchingAsync(target, hash, ct);
    }

    private static async Task PublishSnapshotAsync(string temp, string target, string hash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { FrameworkIO.PublishFile(temp, target); }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(target))
        {
            // Concurrent readers can lock an already valid blob. Reuse it without disrupting transfers.
            using var existing = await OpenMatchingAsync(target, hash, ct);
            if (existing is null) throw;
            File.Delete(temp);
        }
    }

    private static async Task<FileStream?> OpenMatchingAsync(string path, string hash, CancellationToken ct)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (string.Equals(FrameworkIO.Hex(await FrameworkIO.HashAsync(stream, ct)), hash, StringComparison.OrdinalIgnoreCase))
            {
                stream.Position = 0;
                var verified = stream; stream = null; return verified;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { if (stream is not null) stream.Dispose(); }
        return null;
    }
    public async Task<string> PrepareOpenAsync(AttachmentDto file, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (file.Id == Guid.Empty) throw new ArgumentException("Định danh tệp không hợp lệ.");
        if (file.MimeType == FolderAttachment.MimeType) return await PrepareFolderAsync(file, progress, ct);
        _ = BlobPath(file.Sha256);
        var target = OpenPath(file);
        var directory = Path.GetDirectoryName(target)!;
        // A named working copy gives Windows the original extension. Never open/edit the shared immutable blob.
        // Keep an existing working copy, including edits made by the user's associated application.
        if (File.Exists(target)) return target;
        var blob = await DownloadAsync(file, progress, ct);
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, LanChat.Domain.AppIdentity.NewId() + ".part");
        try
        {
            using (var source = new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await source.CopyToAsync(destination, ct);
            if (!await VerifyAsync(temp, file, ct)) throw new IOException("Bản lưu tệp đã thay đổi trong lúc mở. Hãy bấm lại để tải bản đúng.");
            ct.ThrowIfCancellationRequested();
            try { File.Move(temp, target); }
            catch (IOException) when (File.Exists(target)) { /* Another click already prepared this attachment. */ }
            return target;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private string OpenPath(AttachmentDto file)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(file.FileName.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) name = "attachment.bin";
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]))) name = "_" + name;
        var directory = Path.Combine(options.DataDirectory, "Attachments", "Open", file.Id.ToString("N"));
        return Path.Combine(directory, name);
    }

    internal bool IsPrepared(AttachmentDto file) => file.MimeType == FolderAttachment.MimeType
        ? Directory.Exists(OpenPath(file)) : File.Exists(OpenPath(file));

    public async Task<string> DownloadAsync(AttachmentDto file, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (file.FileSize is < 0 or > 20L * 1024 * 1024 * 1024) throw new InvalidDataException("Kích thước tệp không hợp lệ.");
        var key = DeviceIdentity.Decode(file.FileKey, DeviceIdentity.KeyBytes);
        try { return await DownloadWithKeyAsync(file, key, progress, ct); }
        finally { Array.Clear(key, 0, key.Length); }
    }

    private async Task<string> DownloadWithKeyAsync(AttachmentDto file, byte[] fileKey, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var target = BlobPath(file.Sha256);
        var transferId = FileTransferCrypto.TransferId(fileKey);
        using (var local = await OpenVerifiedLocalAsync(file.Sha256, ct))
            if (local is not null && local.Length == file.FileSize) { progress?.Report(new(file.FileSize, file.FileSize)); return target; }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var peers = await store.GetPeerEndpointsAsync(ct);
        foreach (var peer in peers)
        {
            ct.ThrowIfCancellationRequested(); var temp = target + "." + LanChat.Domain.AppIdentity.NewId() + ".part";
            try
            {
                faults.ThrowIfBlocked(peer.NodeId);
                using var client = new TcpClient(AddressFamily.InterNetwork);
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct); handshake.CancelAfter(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(IPAddress.Parse(peer.Address), peer.Port, handshake.Token);
                using var abortHandshake = handshake.Token.Register(() => client.Close());
                using var abortTransfer = ct.Register(() => client.Close());
                using var stream = await tls.ConnectAsync(client.GetStream(), handshake.Token);
                var identity = await chatStore.GetIdentityAsync(ct); var requestId = LanChat.Domain.AppIdentity.NewId();
                await WireProtocol.WriteAsync(stream, wire.Encode(identity.NodeId, "FileDownload", new FileRequest(transferId), requestId), handshake.Token);
                // Source verification/recovery may scan a large file after the connection is established.
                handshake.CancelAfter(TimeSpan.FromMinutes(30));
                var response = await wire.ReadTcpResponseAsync(stream, identity.NodeId, requestId, "FileDownloadResponse", handshake.Token, peer.NodeId);
                var header = WireProtocol.Payload<FileAvailability>(response);
                if (!header.Available || header.Length != file.FileSize) continue;
                abortHandshake.Dispose();
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    await FileTransferCrypto.ReadAsync(stream, output, file.FileSize, file.Sha256, fileKey, progress, client.Close, ct);
                    await output.FlushAsync(ct);
                }
                if (!await VerifyAsync(temp, file, ct)) throw new InvalidDataException("File tải về không khớp SHA256.");
                await PublishSnapshotAsync(temp, target, file.Sha256, ct);
                // Metadata and a matching local hash alone do not authorize serving that blob under a new key.
                await chatStore.RegisterFileKeyAsync(file.Sha256.ToLowerInvariant(), file.FileKey, ct);
                logger.LogInformation("Attachment downloaded and verified: {Bytes} bytes", file.FileSize); return target;
            }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or SocketException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
            { logger.LogWarning("File source unavailable/invalid on {Node}: {Type}", peer.NodeId, ex.GetType().Name); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        throw new IOException("Không tìm được bản gốc còn nguyên vẹn trên các máy đang trực tuyến. Hãy bật máy đã gửi/đã tải tệp rồi thử lại. Nếu bản gốc đã bị sửa hoặc mất, người gửi cần gửi lại tệp thành tin nhắn mới.");
    }
    private static async Task<bool> VerifyAsync(string path, AttachmentDto file, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.FileSize) return false;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return string.Equals(FrameworkIO.Hex(await FrameworkIO.HashAsync(stream, ct)), file.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
