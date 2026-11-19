using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LanChat.Signing;
using Xunit;

namespace LanChat.Tests;

public sealed class SigningTests
{
    [Fact]
    public void VerifierRejectsUnsignedOrTamperedPeWithoutChangingWindowsTrust()
    {
        var executable = typeof(LanChat.WinForms.Forms.MainForm).Assembly.Location;
        X509Certificate2 certificate;
        try { certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(executable)); }
        catch (CryptographicException)
        { Assert.Throws<InvalidDataException>(() => Authenticode.Verify(executable, new string('0', 40))); return; }
        using (certificate)
        {
            var status = Authenticode.Verify(executable, certificate.Thumbprint); Assert.True(status == 0 || status == Authenticode.UntrustedRoot);
            Assert.Throws<InvalidDataException>(() => Authenticode.Verify(executable, new string('0', 40)));
            var directory = Path.Combine(Path.GetTempPath(), "LanChat.SigningTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var tampered = Path.Combine(directory, "tampered.exe"); File.Copy(executable, tampered);
            using (var input = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var reader = new BinaryReader(input))
            {
                input.Position = 0x3c; var pe = reader.ReadInt32(); input.Position = pe + 24 + 16;
                var original = input.ReadByte(); input.Position--; input.WriteByte((byte)(original ^ 1));
            }
            Assert.Throws<InvalidDataException>(() => Authenticode.Verify(tampered, certificate.Thumbprint));
        }
    }
}
