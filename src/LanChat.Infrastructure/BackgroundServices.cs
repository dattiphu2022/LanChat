using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

public sealed class PeerDiscoveryBackgroundService(NodeOptions options, WireProtocol wire, IChatStore store,
    ILocalNetworkInfoProvider networks, ILogger<PeerDiscoveryBackgroundService> logger, NetworkHealth health) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, options.DiscoveryPort));
        }
        catch (SocketException ex)
        {
            health.Error = $"Không mở được UDP {options.DiscoveryPort}: {ex.SocketErrorCode}";
            logger.LogError("Discovery bind failed: {Code}", ex.SocketErrorCode); return;
        }
        logger.LogInformation("UDP discovery started on port {Port}", options.DiscoveryPort);
        await Task.WhenAll(ReceiveAsync(udp, stoppingToken), BroadcastAsync(udp, stoppingToken));
    }
    private async Task BroadcastAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var info = await store.GetNodeInfoAsync(ct);
                var targets = networks.GetNetworks().Select(n => new IPEndPoint(IPAddress.Parse(n.Broadcast), options.DiscoveryPort))
                    .Append(new IPEndPoint(IPAddress.Broadcast, options.DiscoveryPort))
                    .Concat(options.DiscoveryTargets.Select(IPEndPoint.Parse)).Distinct().ToArray();
                foreach (var endpoint in targets)
                {
                    try { await udp.SendAsync(wire.Encode(info.NodeId, "DiscoveryRequest", info), endpoint, ct); }
                    catch (SocketException ex) { logger.LogDebug("Discovery broadcast failed: {Code}", ex.SocketErrorCode); }
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Discovery cycle failed"); await Task.Delay(5000, ct); }
        }
    }
    private async Task ReceiveAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var packet = await udp.ReceiveAsync(ct);
                if (packet.Buffer.Length > 8192) continue;
                var envelope = wire.Decode(packet.Buffer);
                if (envelope.MessageType is not ("DiscoveryRequest" or "DiscoveryResponse")) continue;
                var local = await store.GetNodeInfoAsync(ct);
                if (envelope.SourceNodeId == local.NodeId) continue;
                var peer = WireProtocol.Payload<NodeInfoDto>(envelope);
                if (peer.NodeId != envelope.SourceNodeId) continue;
                await store.RememberPeerAsync(peer, packet.RemoteEndPoint.Address.ToString(), ct);
                if (envelope.MessageType == "DiscoveryRequest")
                    await udp.SendAsync(wire.Encode(local.NodeId, "DiscoveryResponse", local), packet.RemoteEndPoint, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException or ArgumentException or NullReferenceException or SocketException)
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
        await Parallel.ForEachAsync(pending.GroupBy(x => x.RecipientUserId),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (messages, token) =>
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

public sealed class FallbackScanBackgroundService(ILanScanner scanner, ILocalNetworkInfoProvider networks,
    ILogger<FallbackScanBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(10000, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Large networks remain available to an explicit, cancellable manual scan.
                    if (networks.GetNetworks().Sum(x => x.HostCount) <= 4096) await scanner.ScanAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "Fallback scan failed"); }
                await Task.Delay(TimeSpan.FromSeconds(120), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
