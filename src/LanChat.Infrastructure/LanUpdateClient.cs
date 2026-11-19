using System.Net.Sockets;
using LanChat.Application;

namespace LanChat.Infrastructure;

public sealed class LanUpdateClient(ChatStore store, PeerTransport transport, WireProtocol wire, ClusterTls tls) : ILanUpdateClient
{
    public async Task<SignedUpdateManifest> CheckAsync(PeerEndpoint peer, CancellationToken ct = default)
    {
        peer = await ResolveAsync(peer, ct).ConfigureAwait(false);
        UpdateReply reply;
        try { reply = await transport.CallAsync<UpdateReply>(peer, "UpdateInfo", new UpdateInfoRequest(await store.GetNodeInfoAsync(ct)), ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or SocketException or OperationCanceledException)
        { throw new IOException("Máy nguồn chưa hỗ trợ cập nhật LAN hoặc không phản hồi. Hãy kiểm tra phiên bản và kết nối của máy nguồn.", ex); }
        if (reply.Manifest is not { } manifest) throw new IOException(reply.Error ?? "Máy nguồn chưa có bộ cài để chia sẻ.");
        store.VerifyUpdate(manifest, peer.NodeId, ct); return manifest;
    }
    private async Task<PeerEndpoint> ResolveAsync(PeerEndpoint peer, CancellationToken ct)
    {
        if (peer.NodeId == Guid.Empty) throw new ArgumentException("Cần xác minh định danh máy nguồn trước khi tải.");
        var preferred = store.GetPreferredPeerEndpoint(peer, ct);
        Exception? last = null;
        foreach (var endpoint in new[] { preferred, peer }.Concat(store.GetAlternatePeerEndpoints(preferred, ct)).Distinct())
        {
            try
            {
                var info = await transport.GetNodeInfoAsync(endpoint.Address, endpoint.Port, ct).ConfigureAwait(false);
                if (info.NodeId != peer.NodeId) throw new InvalidDataException("IP này thuộc máy khác. Hãy chọn lại máy nguồn cập nhật.");
                await store.RememberPeerAsync(info, endpoint.Address, ct).ConfigureAwait(false);
                return new(info.NodeId, endpoint.Address, info.ServicePort);
            }
            catch (InvalidDataException) { throw; }
            catch (System.Security.Authentication.AuthenticationException ex)
            { throw new IOException("Không xác thực được máy nguồn. Hãy chọn máy thuộc cùng mạng chat.", ex); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or SocketException or OperationCanceledException) { last = ex; }
        }
        throw new IOException("Không kết nối được máy nguồn cập nhật LAN. Hãy bật máy nguồn và thử lại.", last);
    }
    public async Task<Stream> OpenAsync(PeerEndpoint peer, UpdatePackage package, CancellationToken ct = default)
    {
        peer = await ResolveAsync(peer, ct).ConfigureAwait(false);
        var client = new TcpClient(AddressFamily.InterNetwork);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct); lifetime.CancelAfter(TimeSpan.FromSeconds(30));
        var abort = lifetime.Token.Register(client.Close);
        try
        {
            await client.ConnectAsync(System.Net.IPAddress.Parse(peer.Address), peer.Port, lifetime.Token).ConfigureAwait(false);
            var stream = await tls.ConnectAsync(client.GetStream(), lifetime.Token).ConfigureAwait(false);
            try
            {
                var caller = await store.GetNodeInfoAsync(ct).ConfigureAwait(false); var requestId = Guid.NewGuid();
                await WireProtocol.WriteAsync(stream, wire.Encode(caller.NodeId, "UpdateDownload", new UpdateDownloadRequest(caller, package.Id), requestId), lifetime.Token).ConfigureAwait(false);
                var response = await wire.ReadTcpResponseAsync(stream, caller.NodeId, requestId, "UpdateDownloadResponse", lifetime.Token, peer.NodeId).ConfigureAwait(false);
                var reply = WireProtocol.Payload<UpdateReply>(response);
                if (reply.Manifest is not { } manifest) throw new IOException(reply.Error ?? "Máy nguồn chưa có bộ cài.");
                store.VerifyUpdate(manifest, peer.NodeId, ct);
                if (manifest.Package != package) throw new InvalidDataException("Bộ cài đã đổi. Hãy kiểm tra cập nhật lại.");
                lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                return new UpdateReadStream(stream, () => { abort.Dispose(); client.Dispose(); lifetime.Dispose(); });
            }
            catch { stream.Dispose(); throw; }
        }
        catch { abort.Dispose(); client.Dispose(); lifetime.Dispose(); throw; }
    }
}
