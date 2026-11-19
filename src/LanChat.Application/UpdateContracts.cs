namespace LanChat.Application;

public sealed record UpdatePackage(string Tag, long Size, string Digest)
{
    public const string AssetName = "LanChat-Setup-Windows-x64.exe";
    public string Id => Tag + "/" + Digest.Substring(7);
    public string Version => Tag.Substring(1);
    public static bool IsValid(UpdatePackage? package) => package is { Size: > 0 and <= 1073741824, Digest.Length: 71 } &&
        TryVersion(package.Tag, out _) && package.Digest.StartsWith("sha256:", StringComparison.Ordinal) && package.Digest.Skip(7).All(Uri.IsHexDigit);
    public static bool TryVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (tag is null || !tag.StartsWith("v", StringComparison.Ordinal) || !System.Version.TryParse(tag.Substring(1), out var parsed) ||
            parsed.Build < 0 || parsed.Revision >= 0 || tag != "v" + parsed.ToString(3)) return false;
        version = parsed; return true;
    }
}

public sealed record SignedUpdateManifest(UpdatePackage Package, Guid PublisherNodeId, string? Signature = null);
public sealed record UpdateInfoRequest(NodeInfoDto Caller);
public sealed record UpdateDownloadRequest(NodeInfoDto Caller, string PackageId);
public sealed record UpdateReply(SignedUpdateManifest? Manifest = null, string? Error = null);

public interface IUpdatePackageProvider
{
    UpdatePackage? GetPackage(Guid networkId);
    Stream OpenPackage(Guid networkId, string packageId);
}

public interface ILanUpdateClient
{
    Task<SignedUpdateManifest> CheckAsync(PeerEndpoint peer, CancellationToken ct = default);
    Task<Stream> OpenAsync(PeerEndpoint peer, UpdatePackage package, CancellationToken ct = default);
}
