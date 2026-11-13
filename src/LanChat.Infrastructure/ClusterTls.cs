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
            X509KeyStorageFlags.UserKeySet);
        if (!_certificate.HasPrivateKey) throw new InvalidDataException("Chứng chỉ cluster thiếu khóa riêng.");
        _fingerprint = FrameworkIO.Hash(_certificate.RawData);
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
            FrameworkIO.FixedEquals(_fingerprint, FrameworkIO.Hash(certificate.GetRawCertData())));
        try
        {
            using var abort = ct.Register(() => stream.Dispose());
            await ssl.AuthenticateAsClientAsync("LanChat", null, SslProtocols.None, false).ConfigureAwait(false);
            ValidateSession(ssl, ct);
            return ssl;
        }
        catch { ssl.Dispose(); throw; }
    }
    public async Task<SslStream> AcceptAsync(Stream stream, CancellationToken ct)
    {
        var ssl = new SslStream(stream, false);
        try
        {
            using var abort = ct.Register(() => stream.Dispose());
            await ssl.AuthenticateAsServerAsync(_certificate, false, SslProtocols.None, false).ConfigureAwait(false);
            ValidateSession(ssl, ct);
            return ssl;
        }
        catch { ssl.Dispose(); throw; }
    }
    private static void ValidateSession(SslStream ssl, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (ssl.SslProtocol != SslProtocols.Tls12 && (int)ssl.SslProtocol != 12288)
            throw new AuthenticationException("LanChat yêu cầu TLS 1.2 trở lên.");
    }
    public void Dispose() => _certificate.Dispose();
}
