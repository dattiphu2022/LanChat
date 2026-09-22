using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LanChat.Application;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

public sealed class PeerFileTransferService(NodeOptions options, IDistributedStore store, IChatStore chatStore,
    IClusterTransport transport, WireProtocol wire, ClusterTls tls, FaultInjection faults, ILogger<PeerFileTransferService> logger) : IFileTransferService
{
    private string BlobPath(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("SHA256 không hợp lệ.");
        hash = hash.ToLowerInvariant();
        return Path.Combine(options.DataDirectory, "Attachments", hash[..2], hash + ".bin");
    }
    public Task<string?> GetLocalPathAsync(string sha256, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var path = BlobPath(sha256); return Task.FromResult(File.Exists(path) ? path : null);
    }
    public async Task<AttachmentDto> ImportLocalAsync(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 20L * 1024 * 1024 * 1024) throw new ArgumentException("File không tồn tại hoặc vượt quá 20 GB.");
        var tempRoot = Path.Combine(options.DataDirectory, "Attachments", ".temp"); Directory.CreateDirectory(tempRoot);
        var temp = Path.Combine(tempRoot, Guid.CreateVersion7() + ".part");
        try
        {
            await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await source.CopyToAsync(destination, ct);
            string hash;
            await using (var file = File.OpenRead(temp)) hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
            var target = BlobPath(hash); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Replace atomically with the verified copy, including recovery from a damaged local blob.
            File.Move(temp, target, true);
            var mime = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff", ".pdf" => "application/pdf", ".txt" => "text/plain", _ => "application/octet-stream" };
            logger.LogInformation("Imported local attachment {Bytes} bytes", info.Length);
            return new(Guid.CreateVersion7(), Guid.Empty, info.Name, mime, new FileInfo(target).Length, hash);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task<FileAvailability> HasFileAsync(string hash, CancellationToken ct)
    {
        var path = await GetLocalPathAsync(hash, ct); return new(path is not null, path is null ? 0 : new FileInfo(path).Length);
    }
    public async Task<string> PrepareOpenAsync(AttachmentDto file, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (file.Id == Guid.Empty) throw new ArgumentException("Định danh tệp không hợp lệ.");
        _ = BlobPath(file.Sha256);
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(file.FileName.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) name = "attachment.bin";
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]))) name = "_" + name;
        var directory = Path.Combine(options.DataDirectory, "Attachments", "Open", file.Id.ToString("N"));
        var target = Path.Combine(directory, name);
        // A named working copy gives Windows the original extension. Never open/edit the shared immutable blob.
        // Keep an existing working copy, including edits made by the user's associated application.
        if (File.Exists(target)) return target;
        var blob = await DownloadAsync(file, progress, ct);
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, Guid.CreateVersion7() + ".part");
        try
        {
            await using (var source = new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await source.CopyToAsync(destination, ct);
            ct.ThrowIfCancellationRequested();
            try { File.Move(temp, target); }
            catch (IOException) when (File.Exists(target)) { /* Another click already prepared this attachment. */ }
            return target;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task<string> DownloadAsync(AttachmentDto file, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        var target = BlobPath(file.Sha256);
        if (await VerifyAsync(target, file, ct)) { progress?.Report(new(file.FileSize, file.FileSize)); return target; }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var peers = await store.GetPeerEndpointsAsync(ct);
        foreach (var peer in peers)
        {
            ct.ThrowIfCancellationRequested(); var temp = target + "." + Guid.CreateVersion7() + ".part";
            try
            {
                faults.ThrowIfBlocked(peer.NodeId);
                var availability = await transport.CallAsync<FileAvailability>(peer, "FileAvailability", new FileRequest(file.Sha256), ct);
                if (!availability.Available || availability.Length != file.FileSize) continue;
                using var client = new TcpClient(AddressFamily.InterNetwork);
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct); handshake.CancelAfter(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(IPAddress.Parse(peer.Address), peer.Port, handshake.Token);
                await using var stream = await tls.ConnectAsync(client.GetStream(), handshake.Token);
                var identity = await chatStore.GetIdentityAsync(ct); var requestId = Guid.CreateVersion7();
                await WireProtocol.WriteAsync(stream, wire.Encode(identity.NodeId, "FileDownload", new FileRequest(file.Sha256), requestId), handshake.Token);
                var response = wire.Decode(await WireProtocol.ReadAsync(stream, handshake.Token), false);
                if (response.RequestId != requestId || response.SourceNodeId != peer.NodeId || response.MessageType != "FileDownloadResponse") throw new InvalidDataException("Phản hồi tải file không hợp lệ.");
                var header = WireProtocol.Payload<FileAvailability>(response);
                if (!header.Available || header.Length != file.FileSize) continue;
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                {
                    var buffer = new byte[65536]; long received = 0;
                    while (received < file.FileSize)
                    {
                        using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); idleTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                        var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, file.FileSize - received)), idleTimeout.Token);
                        if (count == 0) throw new EndOfStreamException("File bị ngắt giữa chừng.");
                        await output.WriteAsync(buffer.AsMemory(0, count), ct); received += count; progress?.Report(new(received, file.FileSize));
                    }
                    await output.FlushAsync(ct);
                }
                if (!await VerifyAsync(temp, file, ct)) throw new InvalidDataException("File tải về không khớp SHA256.");
                File.Move(temp, target, true);
                logger.LogInformation("Attachment downloaded and verified: {Bytes} bytes", file.FileSize); return target;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
            { logger.LogWarning("File source unavailable/invalid on {Node}: {Type}", peer.NodeId, ex.GetType().Name); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        throw new IOException("File hiện không có trên thiết bị nào đang trực tuyến, hoặc nguồn file không vượt qua kiểm tra SHA256.");
    }
    private static async Task<bool> VerifyAsync(string path, AttachmentDto file, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.FileSize) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)), file.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
