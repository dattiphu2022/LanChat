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
        var prefix = Convert.ToString(bits, 2).Count(bit => bit == '1');
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
    internal static bool Contains(NetworkInfo network, string address) =>
        (ToUInt(address) & ToUInt(network.Mask)) == ToUInt(network.Network);

    internal static bool ContainsHost(NetworkInfo network, string address)
    {
        var ip = ToUInt(address);
        return ip >= ToUInt(network.FirstHost) && ip <= ToUInt(network.LastHost);
    }

    private static uint ToUInt(string address)
    {
        var bytes = IPAddress.Parse(address).GetAddressBytes();
        if (bytes.Length != 4) throw new ArgumentException("Chỉ hỗ trợ IPv4.");
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
    private static string ToAddress(uint value)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
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

public static class ClusterPorts
{
    public static int Alternate(Guid clusterId)
    {
        var hash = FrameworkIO.Hash(clusterId.ToByteArray());
        return 20000 + BinaryPrimitives.ReadUInt16BigEndian(hash) % 28000;
    }
}

public sealed record NetworkEnvelope(Guid RequestId, Guid ClusterId, Guid SourceNodeId, int ProtocolVersion,
    string MessageType, long SentAtSeconds, string PayloadJson, string Mac);

public sealed class WireProtocol(NodeOptions options)
{
    public const int MaxFrame = 4 * 1024 * 1024;
    private readonly HashSet<Guid> _seen = new();
    private readonly SortedDictionary<long, List<Guid>> _expiry = new();
    private readonly object _gate = new();
    public byte[] Encode<T>(Guid source, string type, T payload, Guid? requestId = null)
    {
        var envelope = new NetworkEnvelope(requestId ?? LanChat.Domain.AppIdentity.NewId(), options.ClusterId, source, NodeInfoDto.CurrentProtocol, type,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), JsonSerializer.Serialize(payload), "");
        return JsonSerializer.SerializeToUtf8Bytes(envelope with { Mac = Convert.ToBase64String(Sign(envelope)) });
    }
    private byte[] Sign(NetworkEnvelope value) => FrameworkIO.Hmac(options.ClusterSecret,
        JsonSerializer.SerializeToUtf8Bytes(value with { Mac = "" }));

    public NetworkEnvelope Decode(byte[] bytes, bool rejectReplay = true)
    {
        if (bytes.Length > MaxFrame) throw new InvalidDataException("Gói tin quá lớn.");
        var value = JsonSerializer.Deserialize<NetworkEnvelope>(bytes) ?? throw new InvalidDataException("Gói tin rỗng.");
        if (value.ClusterId != options.ClusterId || value.ProtocolVersion != NodeInfoDto.CurrentProtocol || value.SourceNodeId == Guid.Empty ||
            value.RequestId == Guid.Empty || Math.Abs((double)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - value.SentAtSeconds) > 120 ||
            !FrameworkIO.FixedEquals(Sign(value), Convert.FromBase64String(value.Mac)))
            throw new InvalidDataException("Gói tin không xác thực hoặc khác phiên bản.");
        if (rejectReplay)
        {
            lock (_gate)
            {
                var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 125;
                while (_expiry.Count > 0 && _expiry.First().Key < cutoff)
                {
                    var expired = _expiry.First();
                    foreach (var id in expired.Value) _seen.Remove(id);
                    _expiry.Remove(expired.Key);
                }
                if (_seen.Count >= 30000 || !_seen.Add(value.RequestId)) throw new InvalidDataException("Gói tin phát lại hoặc quá giới hạn.");
                if (!_expiry.TryGetValue(value.SentAtSeconds, out var requests)) _expiry.Add(value.SentAtSeconds, requests = new List<Guid>());
                requests.Add(value.RequestId);
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
    private async Task<NetworkEnvelope> RequestAsync<T>(string address, int port, string type, T body, CancellationToken ct, NodeInfoDto? discovery = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(type == "ControlProposal" ? 8 : 4));
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        if (discovery is not null) timeout.CancelAfter(250);
        await client.ConnectAsync(IPAddress.Parse(address), port, timeout.Token).ConfigureAwait(false);
        if (discovery is not null) timeout.CancelAfter(750);
        using var abort = timeout.Token.Register(() => client.Close());
        using var stream = await tls.ConnectAsync(client.GetStream(), timeout.Token);
        var source = discovery?.NodeId ?? (await store.GetIdentityAsync(timeout.Token)).NodeId;
        var requestId = LanChat.Domain.AppIdentity.NewId();
        await WireProtocol.WriteAsync(stream, wire.Encode(source, type, body, requestId), timeout.Token);
        if (type == "FileAvailability") timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var response = wire.Decode(await WireProtocol.ReadAsync(stream, timeout.Token), false);
        timeout.Token.ThrowIfCancellationRequested();
        if (response.RequestId != requestId || response.MessageType != type + "Response") throw new InvalidDataException("Phản hồi không khớp.");
        return response;
    }
    public async Task<NodeInfoDto> GetNodeInfoAsync(string address, int port, CancellationToken cancellationToken)
    {
        return await DiscoverAsync(address, port, await store.GetNodeInfoAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }
    internal async Task<NodeInfoDto> DiscoverAsync(string address, int port, NodeInfoDto self, CancellationToken cancellationToken)
    {
        NetworkEnvelope response;
        try { response = await RequestAsync(address, port, "NodeInfo", self, cancellationToken, discovery: self).ConfigureAwait(false); }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
        { throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException ex) { throw new IOException("Hết thời gian xác minh máy trong LAN.", ex); }
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
        var listener = new TcpListener(IPAddress.Any, options.ServicePort) { ExclusiveAddressUse = true };
        using var slots = new SemaphoreSlim(32);
        var clients = new List<Task>();
        try
        {
            listener.Start(); using var abort = stoppingToken.Register(listener.Stop); logger.LogInformation("TCP listener started on port {Port}", options.ServicePort);
            while (!stoppingToken.IsCancellationRequested)
            {
                await slots.WaitAsync(stoppingToken);
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch { slots.Release(); throw; }
                clients.RemoveAll(x => x.IsCompleted);
                clients.Add(HandleAsync(client, slots, stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception) when (stoppingToken.IsCancellationRequested) { }
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
            using var abort = timeout.Token.Register(() => client.Close());
            try
            {
                using var stream = await tls.AcceptAsync(client.GetStream(), timeout.Token);
                var request = wire.Decode(await WireProtocol.ReadAsync(stream, timeout.Token));
                faults.ThrowIfBlocked(request.SourceNodeId);
                var remoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                var identity = await store.GetIdentityAsync(timeout.Token);
                object response;
                switch (request.MessageType)
                {
                    case "NodeInfo":
                        using (var body = JsonDocument.Parse(request.PayloadJson))
                            if (body.RootElement.TryGetProperty(nameof(NodeInfoDto.NodeId), out _))
                            {
                                var caller = WireProtocol.Payload<NodeInfoDto>(request);
                                if (caller.NodeId != request.SourceNodeId) throw new InvalidDataException("Sai định danh peer.");
                                await store.RememberPeerAsync(caller, remoteAddress, timeout.Token);
                            }
                        response = await store.GetNodeInfoAsync(timeout.Token); break;
                    case "Digest": response = await services.GetRequiredService<IDistributedStore>().GetDigestAsync(timeout.Token); break;
                    case "EventRange": response = await services.GetRequiredService<IDistributedStore>().GetEventRangeAsync(WireProtocol.Payload<EventRangeRequest>(request), timeout.Token); break;
                    case "EventPush":
                        if (faults.PauseReplication) throw new IOException("Replication paused.");
                        response = await services.GetRequiredService<IDistributedStore>().ImportEventsAsync(WireProtocol.Payload<EventBatch>(request), timeout.Token); break;
                    case "PreVote": response = await services.GetRequiredService<ConsensusService>().PreVoteAsync(WireProtocol.Payload<VoteRequest>(request), request.SourceNodeId, timeout.Token); break;
                    case "Nominate": response = await services.GetRequiredService<ConsensusService>().NominateRemoteAsync(WireProtocol.Payload<Guid>(request), request.SourceNodeId, timeout.Token); break;
                    case "TimeoutNow": response = await services.GetRequiredService<ConsensusService>().AcceptHandoffAsync(WireProtocol.Payload<LeadershipHandoff>(request), request.SourceNodeId, timeout.Token); break;
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
                        using (var input = await files.OpenVerifiedLocalAsync(hash, timeout.Token))
                        {
                            var availability = new FileAvailability(input is not null, input?.Length ?? 0);
                            await WireProtocol.WriteAsync(stream, wire.Encode(identity.NodeId, "FileDownloadResponse", availability, request.RequestId), timeout.Token);
                            if (input is not null) await input.CopyToAsync(stream, timeout.Token);
                        }
                        return;
                    case "ChatPush":
                        var push = WireProtocol.Payload<ChatPushDto>(request);
                        if (push.Sender.NodeId != request.SourceNodeId) throw new InvalidDataException("Sai định danh người gửi.");
                        await store.ReceiveAsync(push, remoteAddress, timeout.Token);
                        response = new ChatAckDto(push.Message.Id);
                        logger.LogDebug("Message received from node {NodeId}", request.SourceNodeId);
                        break;
                    default: throw new InvalidDataException("Loại gói tin chưa hỗ trợ.");
                }
                await WireProtocol.WriteAsync(stream, wire.Encode(identity.NodeId, request.MessageType + "Response", response, request.RequestId), timeout.Token);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or SocketException or JsonException or FormatException or OperationCanceledException or ArgumentException or NullReferenceException or System.Security.Authentication.AuthenticationException)
            { logger.LogDebug("Peer request rejected: {ErrorType}", ex.GetType().Name); }
            catch (Exception ex) { logger.LogError(ex, "Peer request failed"); }
            finally { slots.Release(); }
        }
    }
}
