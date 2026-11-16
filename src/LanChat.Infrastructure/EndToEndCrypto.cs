using System.Security.Cryptography;
using System.Text.Json;
using LanChat.Application;

namespace LanChat.Infrastructure;

internal sealed record WrappedContentKey(Guid UserId, string Ciphertext);
internal sealed record EncryptedContent(string Nonce, string Ciphertext, WrappedContentKey[] Recipients);

internal sealed class DeviceIdentity : IDisposable
{
    private const int RsaBits = 3072;
    private const int RsaBytes = RsaBits / 8;
    private const int EccPublicBytes = 72;
    private const int EccPrivateBytes = 104;
    internal const int KeyBytes = 32;
    internal const int NonceBytes = 12;
    internal const int TagBytes = 16;
    private readonly RSACng _encryption;
    private readonly ECDsaCng _signing;
    private readonly object _keyGate = new();

    private DeviceIdentity(RSACng encryption, ECDsaCng signing)
    {
        _encryption = encryption;
        _signing = signing;
        PublicKeys = new(Convert.ToBase64String(encryption.Key.Export(CngKeyBlobFormat.GenericPublicBlob)),
            Convert.ToBase64String(signing.Key.Export(CngKeyBlobFormat.EccPublicBlob)));
    }

    public DevicePublicKeys PublicKeys { get; }

    internal static DeviceIdentity LoadOrCreate(ChatDbContext db, Guid clusterId)
    {
        using var read = db.Command("SELECT ProtectedKeys FROM DeviceIdentity WHERE Id=1");
        if (read.ExecuteScalar() is byte[] saved)
        {
            var raw = ProtectedData.Unprotect(saved, clusterId.ToByteArray(), DataProtectionScope.CurrentUser);
            try
            {
                if (raw.Length <= EccPrivateBytes) throw new InvalidDataException("Khóa thiết bị bị hỏng.");
                var ecc = raw.Take(EccPrivateBytes).ToArray();
                var rsa = raw.Skip(EccPrivateBytes).ToArray();
                try
                {
                    using var encryptionKey = CngKey.Import(rsa, CngKeyBlobFormat.GenericPrivateBlob);
                    using var signingKey = CngKey.Import(ecc, CngKeyBlobFormat.EccPrivateBlob);
                    return new DeviceIdentity(new RSACng(encryptionKey), new ECDsaCng(signingKey));
                }
                finally { Array.Clear(ecc, 0, ecc.Length); Array.Clear(rsa, 0, rsa.Length); }
            }
            finally { Array.Clear(raw, 0, raw.Length); }
        }

        var encryption = new RSACng(RsaBits);
        var signing = new ECDsaCng(256);
        byte[]? seed = null, privateBlob = null, combined = null;
        try
        {
            var identity = new DeviceIdentity(encryption, signing);
            seed = signing.Key.Export(CngKeyBlobFormat.EccPrivateBlob);
            privateBlob = encryption.Key.Export(CngKeyBlobFormat.GenericPrivateBlob);
            combined = seed.Concat(privateBlob).ToArray();
            db.Execute("INSERT INTO DeviceIdentity VALUES (1,$0)", ProtectedData.Protect(combined, clusterId.ToByteArray(), DataProtectionScope.CurrentUser));
            return identity;
        }
        catch { encryption.Dispose(); signing.Dispose(); throw; }
        finally
        {
            if (seed is not null) Array.Clear(seed, 0, seed.Length);
            if (privateBlob is not null) Array.Clear(privateBlob, 0, privateBlob.Length);
            if (combined is not null) Array.Clear(combined, 0, combined.Length);
        }
    }

    internal string Sign<T>(Guid cluster, string purpose, T value)
    {
        var data = SignatureData(cluster, purpose, value);
        lock (_keyGate) return Convert.ToBase64String(_signing.SignData(data, HashAlgorithmName.SHA256));
    }

    internal static void Verify<T>(Guid cluster, string purpose, T value, DevicePublicKeys? keys, string? signature)
    {
        if (keys is null) throw new InvalidDataException("Thiếu khóa công khai của thiết bị.");
        var signingKey = Decode(keys.SigningKey, EccPublicBytes);
        var proof = Decode(signature, 64);
        var data = SignatureData(cluster, purpose, value);
        try
        {
            using var key = CngKey.Import(signingKey, CngKeyBlobFormat.EccPublicBlob);
            using var signer = new ECDsaCng(key);
            if (signer.KeySize != 256 || !signer.VerifyData(data, proof, HashAlgorithmName.SHA256))
                throw new InvalidDataException("Chữ ký thiết bị không hợp lệ.");
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        { throw new InvalidDataException("Khóa chữ ký không hợp lệ.", ex); }
    }

    private static byte[] SignatureData<T>(Guid cluster, string purpose, T value) =>
        JsonSerializer.SerializeToUtf8Bytes(new { Protocol = "LanChat-v4", Cluster = cluster, Purpose = purpose, Value = value });

    internal static RSACng ImportPublicEncryptionKey(string encoded)
    {
        // A 3072-bit CNG RSA public blob contains a 24-byte header, the exponent, then the modulus.
        var blob = Decode(encoded, RsaBytes + 27);
        try
        {
            using var key = CngKey.Import(blob, CngKeyBlobFormat.GenericPublicBlob);
            var rsa = new RSACng(key);
            if (rsa.KeySize == RsaBits) return rsa;
            rsa.Dispose();
            throw new InvalidDataException("Kích thước khóa mã hóa không hợp lệ.");
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        { throw new InvalidDataException("Khóa mã hóa không hợp lệ.", ex); }
    }

    internal static string Seal(byte[] payload, IEnumerable<(Guid UserId, DevicePublicKeys Keys)> recipients, byte[] context)
    {
        var contentKey = FrameworkIO.RandomBytes(KeyBytes);
        try
        {
            var wrapped = new List<WrappedContentKey>();
            foreach (var (user, keys) in recipients)
            {
                using var rsa = ImportPublicEncryptionKey(keys.EncryptionKey);
                wrapped.Add(new(user, Convert.ToBase64String(rsa.Encrypt(contentKey, RSAEncryptionPadding.OaepSHA256))));
            }
            var nonce = FrameworkIO.RandomBytes(NonceBytes);
            var cipher = Transform(true, contentKey, nonce, context, payload, payload.Length);
            return JsonSerializer.Serialize(new EncryptedContent(Convert.ToBase64String(nonce), Convert.ToBase64String(cipher), wrapped.ToArray()));
        }
        finally { Array.Clear(contentKey, 0, contentKey.Length); }
    }

    internal static EncryptedContent ParseEnvelope(string json)
    {
        var e = JsonSerializer.Deserialize<EncryptedContent>(json) ?? throw new InvalidDataException("Thiếu bản mã.");
        if (e.Recipients is null || e.Recipients.Length is < 1 or > 100 ||
            e.Recipients.Any(x => x is null || x.UserId == Guid.Empty) || e.Recipients.Select(x => x.UserId).Distinct().Count() != e.Recipients.Length ||
            e.Ciphertext is null || e.Ciphertext.Length < 24)
            throw new InvalidDataException("Danh sách khóa người nhận không hợp lệ.");
        _ = Decode(e.Nonce, NonceBytes);
        foreach (var slot in e.Recipients) _ = Decode(slot.Ciphertext, RsaBytes);
        return e;
    }

    internal byte[]? Open(EncryptedContent encrypted, Guid user, byte[] context)
    {
        var slot = encrypted.Recipients.FirstOrDefault(x => x.UserId == user);
        if (slot is null) return null;
        byte[]? key = null;
        try
        {
            lock (_keyGate) key = _encryption.Decrypt(Decode(slot.Ciphertext, RsaBytes), RSAEncryptionPadding.OaepSHA256);
            if (key.Length != KeyBytes) throw new InvalidDataException("Khóa nội dung không hợp lệ.");
            var ciphertext = Convert.FromBase64String(encrypted.Ciphertext);
            return Transform(false, key, Decode(encrypted.Nonce, NonceBytes), context, ciphertext, ciphertext.Length);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { throw new InvalidDataException("Không thể xác thực hoặc giải mã nội dung.", ex); }
        finally { if (key is not null) Array.Clear(key, 0, key.Length); }
    }

    internal static byte[] Transform(bool encrypt, byte[] key, byte[] nonce, byte[] associated, byte[] input, int count)
    {
        var plainLength = encrypt ? count : count - TagBytes;
        if (plainLength < 0) throw new InvalidDataException("Bản mã không đầy đủ.");
        var output = new byte[encrypt ? count + TagBytes : plainLength];
        using var cipher = new AesGcm(key, TagBytes);
        try
        {
            if (encrypt) cipher.Encrypt(nonce, input.AsSpan(0, count), output.AsSpan(0, count), output.AsSpan(count), associated);
            else cipher.Decrypt(nonce, input.AsSpan(0, plainLength), input.AsSpan(plainLength, TagBytes), output, associated);
            return output;
        }
        catch (CryptographicException ex)
        {
            Array.Clear(output, 0, output.Length);
            throw new InvalidDataException("Bản mã đã bị thay đổi hoặc khóa không đúng.", ex);
        }
    }

    internal static byte[] Decode(string? value, int length)
    {
        if (value is null || value.Length != (length + 2) / 3 * 4) throw new InvalidDataException("Dữ liệu mật mã không hợp lệ.");
        try
        {
            var decoded = Convert.FromBase64String(value);
            if (decoded.Length == length && Convert.ToBase64String(decoded) == value) return decoded;
        }
        catch (FormatException) { }
        throw new InvalidDataException("Dữ liệu mật mã không hợp lệ.");
    }

    public void Dispose() { _encryption.Dispose(); _signing.Dispose(); }
}
