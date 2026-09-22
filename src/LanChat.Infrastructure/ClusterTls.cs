using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LanChat.Application;

namespace LanChat.Infrastructure;

public sealed class ClusterTls : IDisposable
{
    private readonly X509Certificate2 _certificate;
    private readonly byte[] _fingerprint;
    public ClusterTls(NodeOptions options)
    {
        if (options.TlsCertificatePfx.Length == 0) throw new InvalidOperationException("Cần nâng cấp mã tham gia để bật kết nối mã hóa.");
        // Schannel requires a key-container-backed private key on Windows for server credentials.
        _certificate = X509CertificateLoader.LoadPkcs12(options.TlsCertificatePfx, null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
        if (!_certificate.HasPrivateKey) throw new InvalidDataException("Chứng chỉ cluster thiếu khóa riêng.");
        _fingerprint = SHA256.HashData(_certificate.RawData);
    }
    public static byte[] CreateCertificate(Guid cluster)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=LanChat-{cluster:N}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return cert.Export(X509ContentType.Pfx);
    }
    public async Task<SslStream> ConnectAsync(Stream stream, CancellationToken ct)
    {
        var ssl = new SslStream(stream, false, (_, certificate, _, _) => certificate is not null &&
            CryptographicOperations.FixedTimeEquals(_fingerprint, SHA256.HashData(certificate.GetRawCertData())));
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "LanChat", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, ct);
            return ssl;
        }
        catch { await ssl.DisposeAsync(); throw; }
    }
    public async Task<SslStream> AcceptAsync(Stream stream, CancellationToken ct)
    {
        var ssl = new SslStream(stream, false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate, ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, ct);
            return ssl;
        }
        catch { await ssl.DisposeAsync(); throw; }
    }
    public void Dispose() => _certificate.Dispose();
}
