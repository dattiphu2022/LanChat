using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

public sealed class PeerDiscoveryBackgroundService(NodeOptions options, WireProtocol wire, IChatStore store,
    ILocalNetworkInfoProvider networks, ILogger<PeerDiscoveryBackgroundService> logger, NetworkHealth health,
    DiscoverySockets sockets, PeerTransport transport) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DiscoveredPeerDto Peer, long VerifiedAt)> _latest = new();
    public DiscoveredPeerDto[] PeersSince(long timestamp) => _latest.Values.Where(p => p.VerifiedAt >= timestamp).Select(p => p.Peer).ToArray();
    public void RequestDiscovery()
    {
        if (_wake.CurrentCount != 0) return;
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DiscoverySockets.Subscription udp;
        try { udp = sockets.Subscribe(options.DiscoveryPort, options.ClusterId); }
        catch (SocketException ex)
        {
            health.Error = $"Không mở được UDP {options.DiscoveryPort}: {ex.SocketErrorCode}";
            logger.LogError("Discovery bind failed: {Code}", ex.SocketErrorCode); return;
        }
        logger.LogInformation("UDP discovery started on port {Port}, network {Cluster}, protocol v{Protocol}", options.DiscoveryPort, options.ClusterId, NodeInfoDto.CurrentProtocol);
        using (udp) await Task.WhenAll(ReceiveAsync(udp, stoppingToken), BroadcastAsync(udp, stoppingToken));
    }
    private async Task BroadcastAsync(DiscoverySockets.Subscription udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var info = await store.GetNodeInfoAsync(ct);
                var targets = networks.GetNetworks().Where(n => n.PrefixLength < 31).Select(n => new IPEndPoint(IPAddress.Parse(n.Broadcast), options.DiscoveryPort))
                    .Append(new IPEndPoint(IPAddress.Broadcast, options.DiscoveryPort))
                    .Concat(options.DiscoveryTargets.Select(target => { var parts = target.Split(':'); return new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1])); })).Distinct().ToArray();
                foreach (var endpoint in targets)
                {
                    try { var request = wire.Encode(info.NodeId, "DiscoveryRequest", info); await udp.SendAsync(request, endpoint); }
                    catch (SocketException ex) { logger.LogDebug("Discovery broadcast failed: {Code}", ex.SocketErrorCode); }
                }
                await _wake.WaitAsync(TimeSpan.FromSeconds(4), ct);
            }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Discovery cycle failed"); await Task.Delay(5000, ct); }
        }
    }
    private async Task ReceiveAsync(DiscoverySockets.Subscription udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var packet = await udp.ReceiveAsync(ct);
                if (packet.Buffer.Length > 8192) continue;
                var envelope = wire.DecodeDiscovery(packet.Buffer, out var clockSkewed);
                var local = await store.GetNodeInfoAsync(ct);
                if (envelope.SourceNodeId == local.NodeId) continue;
                var peer = WireProtocol.Payload<NodeInfoDto>(envelope);
                if (peer.NodeId != envelope.SourceNodeId || peer.ProtocolVersion != envelope.ProtocolVersion ||
                    peer.ServicePort is < 1024 or > 65535 || peer.User is null || peer.User.Id == local.User.Id) continue;
                var address = packet.RemoteEndPoint.Address.ToString();
                if (clockSkewed)
                {
                    // An out-of-window datagram is only a hint. Verify this node live before recording it.
                    var known = _latest.TryGetValue(peer.NodeId, out var previous);
                    if (known && FrameworkIO.Elapsed(previous.VerifiedAt) < TimeSpan.FromSeconds(8) &&
                        previous.Peer.IPAddress == address && previous.Peer.ServicePort == peer.ServicePort)
                        continue;
                    var verified = await transport.DiscoverAsync(address, peer.ServicePort, local, ct, expectedNode: peer.NodeId);
                    if (verified.User is null || verified.User.Id != peer.User.Id) continue;
                    peer = verified;
                    if (!known)
                        logger.LogInformation("Confirmed peer {Node} via TCP after a clock difference greater than 120 seconds", peer.NodeId);
                }
                await store.RememberPeerAsync(peer, address, ct);
                _latest[peer.NodeId] = (LanScanner.ToDiscovered(peer, address, options.ClusterId), Stopwatch.GetTimestamp());
                if (!clockSkewed && envelope.MessageType == "DiscoveryRequest")
                {
                    var reply = wire.Encode(local.NodeId, "DiscoveryResponse", local);
                    await udp.SendAsync(reply, packet.RemoteEndPoint);
                }
            }
            catch (Exception) when (ct.IsCancellationRequested) { break; }
            catch (NetworkRejection ex) { health.NoteRejection(ex, logger); }
            catch (InvalidDataException ex) { logger.LogDebug("Discovery packet rejected: {Reason}", ex.Message); }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException or ArgumentException or NullReferenceException or
                SocketException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
            { logger.LogDebug("Discovery packet rejected: {ErrorType}", ex.GetType().Name); }
            catch (Exception ex) { logger.LogError(ex, "Discovery receive failed"); }
        }
    }
}

public sealed class PendingMessageDeliveryService(IChatStore store, IPeerTransport transport,
    ILogger<PendingMessageDeliveryService> logger) : BackgroundService
{
    public async Task DeliverPendingAsync(CancellationToken ct)
    {
        var pending = await store.GetPendingAsync(ct);
        if (pending.Count == 0) return;
        var sender = await store.GetNodeInfoAsync(ct);
        var users = await store.GetUsersAsync(ct: ct);
        // Separate recipients do not block each other when one endpoint is unreachable.
        await FrameworkIO.ForEachAsync(pending.GroupBy(x => x.RecipientUserId),
            8, ct, async (messages, token) =>
            {
                var peer = users.FirstOrDefault(x => x.UserId == messages.Key);
                foreach (var m in messages)
                {
                    var delivered = false;
                    try
                    {
                        if (peer is not null && peer.ServicePort > 0)
                        {
                            await transport.DeliverAsync(peer.IPAddress, peer.ServicePort, new(sender, ToDto(m)), token);
                            delivered = true;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException or FormatException)
                    { logger.LogDebug("Delivery retry for message {Id}: {ErrorType}", m.Id, ex.GetType().Name); }
                    await store.SetDeliveryAsync(m.Id, delivered, token);
                }
            });
    }
    public static ChatMessageDto ToDto(Message m) => new(m.Id, m.ClientMessageId, m.ConversationId, m.SenderUserId,
        m.RecipientUserId, m.OriginNodeId, m.OriginSequence, m.Kind, m.Text, m.CreatedAtTicks, m.PhysicalTicks, m.LogicalCounter);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DeliverPendingAsync(stoppingToken); await Task.Delay(750, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Pending delivery failed"); await Task.Delay(3000, stoppingToken); }
        }
    }
}
