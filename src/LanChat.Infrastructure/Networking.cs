using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace LanChat.Infrastructure;

public static class NetworkRangeCalculator
{
    public static NetworkInfo Calculate(string address, string mask, string adapter = "", string? gateway = null)
    {
        var ip = ToUInt(address); var bits = ToUInt(mask);
        var inverse = ~bits;
        if ((inverse & (inverse + 1)) != 0) throw new ArgumentException("Subnet mask không liên tục.");
        var network = ip & bits; var broadcast = network | inverse;
        var prefix = System.Numerics.BitOperations.PopCount(bits);
        // RFC 3021 /31 point-to-point and /32 have no broadcast host exclusion.
        var first = prefix >= 31 ? network : network + 1;
        var last = prefix >= 31 ? broadcast : broadcast - 1;
        return new(adapter, address, mask, prefix, ToAddress(network), ToAddress(broadcast), ToAddress(first), ToAddress(last), (long)last - first + 1, gateway);
    }
    public static IEnumerable<string> Hosts(NetworkInfo network)
    {
        var first = ToUInt(network.FirstHost); var last = ToUInt(network.LastHost);
        for (ulong ip = first; ip <= last; ip++)
        {
            var address = ToAddress((uint)ip);
            if (address != network.Address) yield return address;
        }
    }
    private static uint ToUInt(string address)
    {
        var bytes = IPAddress.Parse(address).GetAddressBytes();
        if (bytes.Length != 4) throw new ArgumentException("Chỉ hỗ trợ IPv4.");
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
    private static string ToAddress(uint value)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes).ToString();
    }
}

public sealed class LocalNetworkInfoProvider : ILocalNetworkInfoProvider
{
    public IReadOnlyList<NetworkInfo> GetNetworks()
    {
        var result = new List<NetworkInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
            var name = (nic.Name + " " + nic.Description).ToLowerInvariant();
            if (new[] { "virtual", "vpn", "hyper-v", "vmware", "virtualbox", "tap-", "tunnel", "wireguard", "tailscale" }.Any(name.Contains)) continue;
            var properties = nic.GetIPProperties();
            var gateway = properties.GatewayAddresses.FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            foreach (var ip in properties.UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x.Address)))
                result.Add(NetworkRangeCalculator.Calculate(ip.Address.ToString(), ip.IPv4Mask.ToString(), nic.Name, gateway));
        }
        return result;
    }
}

public sealed record NetworkEnvelope(Guid RequestId, Guid ClusterId, Guid SourceNodeId, int ProtocolVersion,
    string MessageType, long SentAtSeconds, string PayloadJson, string Mac);

public sealed class WireProtocol(NodeOptions options)
{
    public const int MaxFrame = 4 * 1024 * 1024;
    private readonly Dictionary<Guid, long> _seen = [];
    private readonly object _gate = new();
    public byte[] Encode<T>(Guid source, string type, T payload, Guid? requestId = null)
    {
        var envelope = new NetworkEnvelope(requestId ?? Guid.CreateVersion7(), options.ClusterId, source, 2, type,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), JsonSerializer.Serialize(payload), "");
        return JsonSerializer.SerializeToUtf8Bytes(envelope with { Mac = Convert.ToBase64String(Sign(envelope)) });
    }
    private byte[] Sign(NetworkEnvelope value) => HMACSHA256.HashData(options.ClusterSecret,
        JsonSerializer.SerializeToUtf8Bytes(value with { Mac = "" }));

    public NetworkEnvelope Decode(byte[] bytes, bool rejectReplay = true)
    {
        if (bytes.Length > MaxFrame) throw new InvalidDataException("Gói tin quá lớn.");
        var value = JsonSerializer.Deserialize<NetworkEnvelope>(bytes) ?? throw new InvalidDataException("Gói tin rỗng.");
        if (value.ClusterId != options.ClusterId || value.ProtocolVersion != 2 || value.SourceNodeId == Guid.Empty ||
            value.RequestId == Guid.Empty || Math.Abs((double)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - value.SentAtSeconds) > 120 ||
            !CryptographicOperations.FixedTimeEquals(Sign(value), Convert.FromBase64String(value.Mac)))
            throw new InvalidDataException("Gói tin không xác thực hoặc khác phiên bản.");
        if (rejectReplay)
        {
            lock (_gate)
            {
                var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 125;
                foreach (var id in _seen.Where(x => x.Value < cutoff).Select(x => x.Key).ToArray()) _seen.Remove(id);
                if (_seen.Count >= 10000 || !_seen.TryAdd(value.RequestId, value.SentAtSeconds)) throw new InvalidDataException("Gói tin phát lại hoặc quá giới hạn.");
            }
        }
        return value;
    }
    public static T Payload<T>(NetworkEnvelope envelope) => JsonSerializer.Deserialize<T>(envelope.PayloadJson) ?? throw new InvalidDataException("Thiếu nội dung gói tin.");
    public static async Task WriteAsync(Stream stream, byte[] frame, CancellationToken ct)
    {
        if (frame.Length > MaxFrame) throw new InvalidDataException("Gói tin quá lớn.");
        var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, frame.Length);
        await stream.WriteAsync(length, ct); await stream.WriteAsync(frame, ct);
    }
    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken ct)
    {
        var length = new byte[4]; await stream.ReadExactlyAsync(length, ct);
        var count = BinaryPrimitives.ReadInt32BigEndian(length);
        if (count is <= 0 or > MaxFrame) throw new InvalidDataException("Độ dài gói tin không hợp lệ.");
        var data = new byte[count]; await stream.ReadExactlyAsync(data, ct); return data;
    }
}

public sealed class PeerTransport(WireProtocol wire, IChatStore store, FaultInjection faults, ClusterTls tls) : IPeerTransport, IClusterTransport
{
    private async Task<NetworkEnvelope> RequestAsync<T>(string address, int port, string type, T body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(type == "ControlProposal" ? 8 : 4));
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await client.ConnectAsync(IPAddress.Parse(address), port, timeout.Token);
        await using var stream = await tls.ConnectAsync(client.GetStream(), timeout.Token);
        var identity = await store.GetIdentityAsync(timeout.Token);
        var requestId = Guid.CreateVersion7();
        await WireProtocol.WriteAsync(stream, wire.Encode(identity.NodeId, type, body, requestId), timeout.Token);
        if (type == "FileAvailability") timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var response = wire.Decode(await WireProtocol.ReadAsync(stream, timeout.Token), false);
        if (response.RequestId != requestId || response.MessageType != type + "Response") throw new InvalidDataException("Phản hồi không khớp.");
        return response;
    }
    public async Task<NodeInfoDto> GetNodeInfoAsync(string address, int port, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(address, port, "NodeInfo", new { }, cancellationToken);
        var info = WireProtocol.Payload<NodeInfoDto>(response);
        if (info.NodeId != response.SourceNodeId) throw new InvalidDataException("Sai định danh peer.");
        return info;
    }
    public async Task DeliverAsync(string address, int port, ChatPushDto message, CancellationToken cancellationToken)
    {
        var response = await RequestAsync(address, port, "ChatPush", message, cancellationToken);
        if (WireProtocol.Payload<ChatAckDto>(response).MessageId != message.Message.Id) throw new InvalidDataException("ACK không khớp.");
    }
    public async Task<T> CallAsync<T>(PeerEndpoint peer, string type, object payload, CancellationToken ct)
    {
        faults.ThrowIfBlocked(peer.NodeId);
        var response = await RequestAsync(peer.Address, peer.Port, type, payload, ct);
        if (peer.NodeId != Guid.Empty && response.SourceNodeId != peer.NodeId) throw new InvalidDataException("Endpoint đã đổi định danh.");
        return WireProtocol.Payload<T>(response);
    }
}

public sealed class NetworkHealth
{
    public string? Error { get; set; }
}

public sealed class PeerConnectionManager(NodeOptions options, WireProtocol wire, IChatStore store,
    ILogger<PeerConnectionManager> logger, NetworkHealth health, IServiceProvider services, FaultInjection faults, ClusterTls tls) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, options.ServicePort);
        using var slots = new SemaphoreSlim(32);
        var clients = new List<Task>();
        try
        {
            listener.Start(); logger.LogInformation("TCP listener started on port {Port}", options.ServicePort);
            while (!stoppingToken.IsCancellationRequested)
            {
                await slots.WaitAsync(stoppingToken);
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stoppingToken); }
                catch { slots.Release(); throw; }
                clients.RemoveAll(x => x.IsCompleted);
                clients.Add(HandleAsync(client, slots, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (SocketException ex)
        {
            health.Error = $"Không mở được TCP {options.ServicePort}: {ex.SocketErrorCode}";
            logger.LogError("TCP listener failed: {Code}", ex.SocketErrorCode);
        }
        finally { listener.Stop(); await Task.WhenAll(clients); }
    }
    private async Task HandleAsync(TcpClient client, SemaphoreSlim slots, CancellationToken ct)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await using var stream = await tls.AcceptAsync(client.GetStream(), timeout.Token);
                var request = wire.Decode(await WireProtocol.ReadAsync(stream, timeout.Token));
                faults.ThrowIfBlocked(request.SourceNodeId);
                var identity = await store.GetIdentityAsync(timeout.Token);
                object response;
                switch (request.MessageType)
                {
                    case "NodeInfo": response = await store.GetNodeInfoAsync(timeout.Token); break;
                    case "Digest": response = await services.GetRequiredService<IDistributedStore>().GetDigestAsync(timeout.Token); break;
                    case "EventRange": response = await services.GetRequiredService<IDistributedStore>().GetEventRangeAsync(WireProtocol.Payload<EventRangeRequest>(request), timeout.Token); break;
                    case "EventPush":
                        if (faults.PauseReplication) throw new IOException("Replication paused.");
                        response = await services.GetRequiredService<IDistributedStore>().ImportEventsAsync(WireProtocol.Payload<EventBatch>(request), timeout.Token); break;
                    case "Vote": response = await services.GetRequiredService<ConsensusService>().VoteAsync(WireProtocol.Payload<VoteRequest>(request), request.SourceNodeId, timeout.Token); break;
                    case "Append": response = await services.GetRequiredService<ConsensusService>().AppendAsync(WireProtocol.Payload<AppendRequest>(request), request.SourceNodeId, timeout.Token); break;
                    case "ConsensusStatus": response = await services.GetRequiredService<IConsensusService>().GetStatusAsync(timeout.Token); break;
                    case "ControlProposal": response = await services.GetRequiredService<ConsensusService>().ProposeRemoteAsync(WireProtocol.Payload<ControlCommand>(request), request.SourceNodeId, timeout.Token); break;
                    case "FileAvailability":
                        timeout.CancelAfter(TimeSpan.FromMinutes(30));
                        response = await services.GetRequiredService<PeerFileTransferService>().HasFileAsync(WireProtocol.Payload<FileRequest>(request).Sha256, timeout.Token); break;
                    case "FileDownload":
                        var files = services.GetRequiredService<PeerFileTransferService>(); var hash = WireProtocol.Payload<FileRequest>(request).Sha256;
                        timeout.CancelAfter(TimeSpan.FromMinutes(30));
                        await using (var input = await files.OpenVerifiedLocalAsync(hash, timeout.Token))
                        {
                            var availability = new FileAvailability(input is not null, input?.Length ?? 0);
                            await WireProtocol.WriteAsync(stream, wire.Encode(identity.NodeId, "FileDownloadResponse", availability, request.RequestId), timeout.Token);
                            if (input is not null) await input.CopyToAsync(stream, timeout.Token);
                        }
                        return;
                    case "ChatPush":
                        var push = WireProtocol.Payload<ChatPushDto>(request);
                        if (push.Sender.NodeId != request.SourceNodeId) throw new InvalidDataException("Sai định danh người gửi.");
                        await store.ReceiveAsync(push, ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString(), timeout.Token);
                        response = new ChatAckDto(push.Message.Id);
                        logger.LogDebug("Message received from node {NodeId}", request.SourceNodeId);
                        break;
                    default: throw new InvalidDataException("Loại gói tin chưa hỗ trợ.");
                }
                await WireProtocol.WriteAsync(stream, wire.Encode(identity.NodeId, request.MessageType + "Response", response, request.RequestId), timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or FormatException or OperationCanceledException or ArgumentException or NullReferenceException or System.Security.Authentication.AuthenticationException)
            { logger.LogDebug("Peer request rejected: {ErrorType}", ex.GetType().Name); }
            catch (Exception ex) { logger.LogError(ex, "Peer request failed"); }
            finally { slots.Release(); }
        }
    }
}

public sealed class LanScanner(ILocalNetworkInfoProvider networks, IPeerTransport transport, IChatStore store,
    NodeOptions options, ILogger<LanScanner> logger) : ILanScanner
{
    private readonly SemaphoreSlim _scan = new(1, 1);
    public async Task<IReadOnlyList<DiscoveredPeerDto>> ScanAsync(CancellationToken cancellationToken = default)
    {
        if (!await _scan.WaitAsync(0, cancellationToken)) return [];
        try
        {
            var ranges = networks.GetNetworks();
            var localIps = ranges.Select(x => x.Address).ToHashSet();
            var result = new ConcurrentBag<DiscoveredPeerDto>();
            var own = await store.GetIdentityAsync(cancellationToken);
            logger.LogInformation("LAN scan started for {Count} interfaces", ranges.Count);
            // Enumerated lazily: even a large subnet does not allocate every address at once.
            await Parallel.ForEachAsync(ranges.SelectMany(NetworkRangeCalculator.Hosts).Where(x => !localIps.Contains(x)),
                new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = cancellationToken }, async (address, ct) =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(600);
                    try
                    {
                        var info = await transport.GetNodeInfoAsync(address, options.ServicePort, timeout.Token);
                        if (info.NodeId == own.NodeId) return;
                        await store.RememberPeerAsync(info, address, ct);
                        result.Add(new(info.NodeId, info.User.Id, info.User.DisplayName, info.DeviceName, address,
                            info.ServicePort, NodeRole.Follower, 0, info.ProtocolVersion, info.ApplicationVersion, options.ClusterId));
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException or FormatException or System.Security.Authentication.AuthenticationException) { }
                });
            logger.LogInformation("LAN scan completed: {Count} peers", result.Count);
            return result.ToArray();
        }
        finally { _scan.Release(); }
    }
}
