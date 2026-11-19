using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace LanChat.Signing;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2 || args[0] is not ("sign" or "verify")) throw new ArgumentException("Usage: LanChat.Signing.exe sign|verify path.exe [path.exe ...]");
            var explicitThumbprint = args.Length >= 4 && args[1] == "--thumbprint" ? args[2] : null;
            using var certificate = DevelopmentCertificate(explicitThumbprint);
            var tool = FindSignTool();
            foreach (var argument in args.Skip(explicitThumbprint is null ? 1 : 3))
            {
                var path = Path.GetFullPath(argument);
                var innoTemporary = Path.GetFileName(path).EndsWith(".e32.tmp", StringComparison.OrdinalIgnoreCase);
                if (!File.Exists(path) || (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && !innoTemporary)) throw new IOException("An existing EXE or Inno Setup PE temporary file is required.");
                var product = FileVersionInfo.GetVersionInfo(path).ProductName?.Trim();
                if (product is not ("LanChat" or "LanChat.WinForms")) throw new InvalidOperationException("Refusing to sign a non-LanChat product: " + path);
                if (args[0] == "sign")
                {
                    var signed = Run(tool, "sign", "/s", "My", "/sha1", certificate.Thumbprint, "/fd", "SHA256", "/d", "LanChat Development", path);
                    if (signed.Code != 0) throw new InvalidOperationException("SignTool failed: " + signed.Text);
                }
                var status = Authenticode.Verify(path, certificate.Thumbprint);
                var check = Run(tool, "verify", "/pa", "/v", path);
                if ((status == 0 && check.Code != 0) || (status != 0 && status != Authenticode.UntrustedRoot)) throw new InvalidOperationException("Unexpected verification result.");
                using var input = File.OpenRead(path); using var sha = SHA256.Create();
                Console.WriteLine("SIGNED_FILE=" + path);
                Console.WriteLine("THUMBPRINT=" + certificate.Thumbprint);
                Console.WriteLine("SHA256=" + BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant());
                Console.WriteLine(status == 0 ? "VERIFY=Valid" : "VERIFY=Cryptographic signature and PE integrity checked; self-signed development root is not trusted by Windows (0x800B0109).");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static X509Certificate2 DevelopmentCertificate(string? thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser); store.Open(OpenFlags.ReadOnly);
        var suitable = store.Certificates.Cast<X509Certificate2>().Where(c => (thumbprint is null || c.Thumbprint == thumbprint) && c.Subject == "CN=LanChat Development" && c.Issuer == c.Subject &&
            c.HasPrivateKey && c.NotBefore <= DateTime.Now && c.NotAfter > DateTime.Now && c.SignatureAlgorithm.Value == "1.2.840.113549.1.1.11").OrderByDescending(c => c.NotAfter);
        foreach (var certificate in suitable)
        {
            using var rsa = certificate.GetRSAPublicKey();
            if (rsa?.KeySize == 3072 && certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.3")))
                return certificate;
        }
        throw new InvalidOperationException("No suitable LanChat Development code signing certificate in CurrentUser/My. Initialize and back it up first.");
    }
    private static string FindSignTool()
    {
        var root = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Kits\Installed Roots", "KitsRoot10", null) as string ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10");
        var candidates = Directory.GetFiles(Path.Combine(root, "bin"), "signtool.exe", SearchOption.AllDirectories)
            .Where(p => string.Equals(Path.GetFileName(Path.GetDirectoryName(p)), "x64", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => { var v = FileVersionInfo.GetVersionInfo(p); return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart); });
        return candidates.FirstOrDefault() ?? throw new FileNotFoundException("SignTool x64 was not found in the Windows SDK.");
    }
    private static (int Code, string Text) Run(string executable, params string[] arguments)
    {
        if (arguments.Any(a => a.IndexOfAny(['"', '\r', '\n']) >= 0)) throw new ArgumentException("Invalid command argument.");
        using var process = Process.Start(new ProcessStartInfo(executable, string.Join(" ", arguments.Select(a => "\"" + a + "\"")))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); process.WaitForExit(); Task.WaitAll(output, error);
        return (process.ExitCode, output.Result + error.Result);
    }
}

// Verification is local and does not import certificates or change Windows trust.
public static class Authenticode
{
    public const uint UntrustedRoot = 0x800B0109;
    public static uint Verify(string path, string thumbprint)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(input);
        input.Position = 0x3c; var pe = reader.ReadInt32();
        if (pe < 0x40 || pe > input.Length - 160) throw new InvalidDataException("Invalid PE header.");
        input.Position = pe; if (reader.ReadUInt32() != 0x4550) throw new InvalidDataException("Invalid PE signature.");
        input.Position = pe + 24; var magic = reader.ReadUInt16();
        if (magic is not (0x10b or 0x20b)) throw new InvalidDataException("Unsupported PE format.");
        input.Position = pe + 24 + (magic == 0x20b ? 112 : 96) + 32;
        var offset = reader.ReadUInt32(); var length = reader.ReadUInt32();
        if (offset == 0 || length < 8 || (long)offset + length > input.Length) throw new InvalidDataException("No valid embedded certificate table.");
        input.Position = offset; var certificateLength = reader.ReadUInt32(); var revision = reader.ReadUInt16(); var type = reader.ReadUInt16();
        if (revision != 0x200 || type != 2 || certificateLength < 8 || certificateLength > length || certificateLength > 1024 * 1024) throw new InvalidDataException("Invalid Authenticode certificate.");
        var cms = new SignedCms(); cms.Decode(reader.ReadBytes((int)certificateLength - 8)); cms.CheckSignature(verifySignatureOnly: true);
        if (cms.SignerInfos.Count != 1 || cms.SignerInfos[0].Certificate?.Thumbprint != thumbprint ||
            cms.ContentInfo.ContentType.Value != "1.3.6.1.4.1.311.2.1.4" || cms.SignerInfos[0].DigestAlgorithm.Value != "2.16.840.1.101.3.4.2.1")
            throw new InvalidDataException("The signature does not match the expected SHA-256 LanChat certificate.");
        var file = new TrustFile { Size = (uint)Marshal.SizeOf(typeof(TrustFile)), Path = Path.GetFullPath(path) };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustFile)));
        try
        {
            Marshal.StructureToPtr(file, pointer, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf(typeof(TrustData)), UiChoice = 2, UnionChoice = 1, File = pointer,
                StateAction = 1, ProviderFlags = 0x1000 | 0x10 };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            uint result;
            try { result = WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
            finally { data.StateAction = 2; WinVerifyTrust(new IntPtr(-1), ref action, ref data); }
            if (result != 0 && result != UntrustedRoot) throw new InvalidDataException("PE signature verification failed: 0x" + result.ToString("X8"));
            return result;
        }
        finally { Marshal.DestroyStructure(pointer, typeof(TrustFile)); Marshal.FreeHGlobal(pointer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr Handle, KnownSubject; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustData
    {
        public uint Size; public IntPtr PolicyCallback, SipClient; public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File; public uint StateAction; public IntPtr StateData, Url; public uint ProviderFlags, UiContext; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint WinVerifyTrust(IntPtr owner, ref Guid action, ref TrustData data);
}
