using LanChat.Application;

namespace LanChat.Infrastructure;

public sealed partial class ChatStore
{
    internal async Task<SignedUpdateManifest> SignUpdateAsync(UpdatePackage package, CancellationToken ct)
    {
        var identity = await GetIdentityAsync(ct).ConfigureAwait(false);
        var manifest = new SignedUpdateManifest(package, identity.NodeId);
        return manifest with { Signature = Device.Sign(options.ClusterId, "update-package", manifest) };
    }
    internal void VerifyUpdate(SignedUpdateManifest manifest, Guid nodeId, CancellationToken ct)
    {
        if (manifest.PublisherNodeId != nodeId || !UpdatePackage.IsValid(manifest.Package)) throw new InvalidDataException("Thông tin cập nhật không khớp máy nguồn.");
        using var db = database.Open(ct);
        var peer = db.Required<LanChat.Domain.PeerNode>("NodeId=$0", nodeId);
        var keys = FindDeviceKeys(db, peer.UserId);
        DeviceIdentity.Verify(options.ClusterId, "update-package", manifest with { Signature = null }, keys, manifest.Signature);
    }
}
