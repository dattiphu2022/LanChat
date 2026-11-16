using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LanChat.Application;

namespace LanChat.Infrastructure;

internal static class FileTransferCrypto
{
    private const int ChunkBytes = 64 * 1024;
    private const int SaltBytes = 32;
    private const int TagBytes = DeviceIdentity.TagBytes;
    private static readonly byte[] ExpandInfo = Encoding.ASCII.GetBytes("LanChat-v4/file-session\x01");

    internal static string TransferId(byte[] key) => FrameworkIO.Hex(FrameworkIO.Hash(key)).ToLowerInvariant();

    private static byte[] SessionKey(byte[] key, byte[] salt)
    {
        // HKDF-SHA256 extract and a single expand block for a 256-bit session key (RFC 5869).
        var prk = FrameworkIO.Hmac(salt, key);
        try { return FrameworkIO.Hmac(prk, ExpandInfo); }
        finally { Array.Clear(prk, 0, prk.Length); }
    }

    private static byte[] Metadata(string hash, long length) =>
        Encoding.ASCII.GetBytes("LanChat-v4/file/" + hash.ToLowerInvariant() + "/" + length.ToString(CultureInfo.InvariantCulture));

    internal static async Task WriteAsync(Stream plaintext, Stream network, long length, string hash, byte[] key, CancellationToken ct)
    {
        var salt = FrameworkIO.RandomBytes(SaltBytes);
        await network.WriteAsync(salt, ct).ConfigureAwait(false);
        var sessionKey = SessionKey(key, salt);
        var clear = new byte[ChunkBytes];
        var encrypted = new byte[ChunkBytes + TagBytes];
        var nonce = new byte[DeviceIdentity.NonceBytes];
        var metadata = Metadata(hash, length);
        try
        {
            using var aes = new AesGcm(sessionKey, TagBytes);
            long left = length;
            uint sequence = 0;
            do
            {
                var count = (int)Math.Min(left, ChunkBytes);
                await ReadExactlyAsync(plaintext, clear, count, ct).ConfigureAwait(false);
                BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), sequence++);
                aes.Encrypt(nonce, clear.AsSpan(0, count), encrypted.AsSpan(0, count), encrypted.AsSpan(count, TagBytes), metadata);
                await network.WriteAsync(encrypted, 0, count + TagBytes, ct).ConfigureAwait(false);
                left -= count;
            } while (left > 0);
        }
        finally { Array.Clear(sessionKey, 0, sessionKey.Length); Array.Clear(clear, 0, clear.Length); }
    }

    internal static async Task ReadAsync(Stream network, Stream plaintext, long length, string hash, byte[] key,
        IProgress<FileProgress>? progress, Action abort, CancellationToken ct)
    {
        var salt = new byte[SaltBytes];
        await ReadFromNetworkAsync(network, salt, salt.Length, abort, ct).ConfigureAwait(false);
        var sessionKey = SessionKey(key, salt);
        var clear = new byte[ChunkBytes];
        var encrypted = new byte[ChunkBytes + TagBytes];
        var nonce = new byte[DeviceIdentity.NonceBytes];
        var metadata = Metadata(hash, length);
        try
        {
            using var aes = new AesGcm(sessionKey, TagBytes);
            long completed = 0;
            uint sequence = 0;
            do
            {
                var count = (int)Math.Min(length - completed, ChunkBytes);
                await ReadFromNetworkAsync(network, encrypted, count + TagBytes, abort, ct).ConfigureAwait(false);
                BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), sequence++);
                aes.Decrypt(nonce, encrypted.AsSpan(0, count), encrypted.AsSpan(count, TagBytes), clear.AsSpan(0, count), metadata);
                await plaintext.WriteAsync(clear, 0, count, ct).ConfigureAwait(false);
                completed += count;
                progress?.Report(new(completed, length));
            } while (completed < length);
        }
        catch (CryptographicException ex) { throw new InvalidDataException("Tệp mã hóa đã bị thay đổi hoặc khóa không đúng.", ex); }
        finally { Array.Clear(sessionKey, 0, sessionKey.Length); Array.Clear(clear, 0, clear.Length); }
    }

    private static async Task ReadFromNetworkAsync(Stream stream, byte[] buffer, int length, Action abort, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var close = deadline.Token.Register(abort);
        await ReadExactlyAsync(stream, buffer, length, deadline.Token).ConfigureAwait(false);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int length, CancellationToken ct)
    {
        for (var offset = 0; offset < length;)
        {
            var count = await stream.ReadAsync(buffer, offset, length - offset, ct).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Tệp bị ngắt giữa chừng.");
            offset += count;
        }
    }
}
