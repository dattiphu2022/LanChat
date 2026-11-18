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

internal enum NetworkRejectionKind { Signature, ProtocolVersion }

internal sealed class NetworkRejection(NetworkRejectionKind kind, string message) : Exception(message)
{
    public NetworkRejectionKind Kind { get; } = kind;
}

public sealed class WireProtocol
{
    public const int MaxFrame = 4 * 1024 * 1024;
    private const string ClockChallenge = "ClockChallenge";
    private const string ClockProof = "ClockProof";
    private const int FreshnessSeconds = 120;
    private readonly NodeOptions _options;
    private readonly Func<long> _utcSeconds;
    private readonly HashSet<Guid> _seen = new();
    private readonly SortedDictionary<long, List<Guid>> _expiry = new();
    private readonly object _gate = new();

    public WireProtocol(NodeOptions options) : this(options, () => DateTimeOffset.UtcNow.ToUnixTimeSeconds()) { }

    internal WireProtocol(NodeOptions options, Func<long> utcSeconds)
    { _options = options; _utcSeconds = utcSeconds; }

    public byte[] Encode<T>(Guid source, string type, T payload, Guid? requestId = null)
    {
        var envelope = new NetworkEnvelope(requestId ?? LanChat.Domain.AppIdentity.NewId(), _options.ClusterId, source,
            NodeInfoDto.CurrentProtocol, type, _utcSeconds(), JsonSerializer.Serialize(payload), "");
        return JsonSerializer.SerializeToUtf8Bytes(envelope with { Mac = Convert.ToBase64String(Sign(envelope)) });
    }

    private byte[] Sign(NetworkEnvelope value) => FrameworkIO.Hmac(_options.ClusterSecret,
        JsonSerializer.SerializeToUtf8Bytes(value with { Mac = "" }));

    public NetworkEnvelope Decode(byte[] bytes, bool rejectReplay = true)
    {
        NetworkEnvelope value;
        try { value = Authenticate(bytes); }
        catch (NetworkRejection ex) { throw new InvalidDataException(ex.Message, ex); }
        var now = _utcSeconds();
        if (!IsFresh(value, now)) throw new InvalidDataException("Giờ giữa hai máy lệch quá 120 giây.");
        if (rejectReplay) RememberRequest(value.RequestId, value.SentAtSeconds, now);
        return value;
    }

    internal NetworkEnvelope DecodeDiscovery(byte[] bytes, out bool clockSkewed)
    {
        var value = Authenticate(bytes);
        if (value.MessageType is not ("DiscoveryRequest" or "DiscoveryResponse"))
            throw new InvalidDataException("Loại gói tin UDP không hợp lệ.");
        var now = _utcSeconds();
        clockSkewed = !IsFresh(value, now);
        if (!clockSkewed) RememberRequest(value.RequestId, value.SentAtSeconds, now);
        return value;
    }

    private NetworkEnvelope Authenticate(byte[] bytes)
    {
        if (bytes.Length > MaxFrame) throw new InvalidDataException("Gói tin quá lớn.");
        var value = JsonSerializer.Deserialize<NetworkEnvelope>(bytes) ?? throw new InvalidDataException("Gói tin rỗng.");
        if (value.ClusterId != _options.ClusterId) throw new InvalidDataException("Gói tin thuộc mạng khác.");
        if (value.SourceNodeId == Guid.Empty || value.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(value.MessageType))
            throw new InvalidDataException("Gói tin thiếu định danh.");
        if (value.Mac is not { Length: 44 } || !FrameworkIO.FixedEquals(Sign(value), Convert.FromBase64String(value.Mac)))
            throw new NetworkRejection(NetworkRejectionKind.Signature, "Chữ ký của gói tin không khớp mã mạng.");
        if (value.ProtocolVersion != NodeInfoDto.CurrentProtocol)
            throw new NetworkRejection(NetworkRejectionKind.ProtocolVersion,
                $"Máy bên kia dùng giao thức v{value.ProtocolVersion}, máy này dùng v{NodeInfoDto.CurrentProtocol}.");
        return value;
    }

    private static bool IsFresh(NetworkEnvelope value, long now) => Math.Abs((double)now - value.SentAtSeconds) <= FreshnessSeconds;

    private void RememberRequest(Guid requestId, long sentAt, long now)
    {
        lock (_gate)
        {
            while (_expiry.Count > 0 && _expiry.First().Key < now)
            {
                var expired = _expiry.First();
                foreach (var id in expired.Value) _seen.Remove(id);
                _expiry.Remove(expired.Key);
            }
            if (_seen.Count >= 30000 || !_seen.Add(requestId)) throw new InvalidDataException("Gói tin phát lại hoặc quá giới hạn.");
            var expires = Math.Max(now, sentAt) + FreshnessSeconds + 5;
            if (!_expiry.TryGetValue(expires, out var requests)) _expiry.Add(expires, requests = new List<Guid>());
            requests.Add(requestId);
        }
    }

    internal async Task<NetworkEnvelope> ReadTcpRequestAsync(Stream stream, Guid localNodeId, CancellationToken ct)
    {
        var request = Authenticate(await ReadAsync(stream, ct).ConfigureAwait(false));
        var now = _utcSeconds();
        if (IsFresh(request, now))
        {
            RememberRequest(request.RequestId, request.SentAtSeconds, now);
            return request;
        }

        // A stale request must answer a fresh nonce on this TLS connection before it is processed.
        var challengeId = Guid.NewGuid();
        await WriteAsync(stream, Encode(localNodeId, ClockChallenge, request.RequestId, challengeId), ct)
            .ConfigureAwait(false);
        var proof = Authenticate(await ReadAsync(stream, ct).ConfigureAwait(false));
        if (proof.RequestId != challengeId || proof.SourceNodeId != request.SourceNodeId || proof.MessageType != ClockProof ||
            Payload<Guid>(proof) != request.RequestId)
            throw new InvalidDataException("Phản hồi xác thực giờ kết nối không hợp lệ.");
        now = _utcSeconds();
        RememberRequest(request.RequestId, now, now);
        return request;
    }

    internal async Task<NetworkEnvelope> ReadTcpResponseAsync(Stream stream, Guid source, Guid requestId, string responseType, CancellationToken ct,
        Guid? expectedNode = null)
    {
        var response = DecodeTcpResponse(await ReadAsync(stream, ct).ConfigureAwait(false));
        if (response.MessageType == ClockChallenge)
        {
            if (response.RequestId == requestId || Payload<Guid>(response) != requestId ||
                (expectedNode.HasValue && response.SourceNodeId != expectedNode.Value))
                throw new InvalidDataException("Yêu cầu xác thực giờ không khớp kết nối.");
            var challenge = response;
            await WriteAsync(stream, Encode(source, ClockProof, requestId, challenge.RequestId), ct)
                .ConfigureAwait(false);
            response = DecodeTcpResponse(await ReadAsync(stream, ct).ConfigureAwait(false));
            if (response.SourceNodeId != challenge.SourceNodeId) throw new InvalidDataException("Máy phản hồi đã đổi định danh.");
        }

        // A signed response on the pinned TLS connection is tied to this fresh request ID.
        if (response.RequestId != requestId || response.MessageType != responseType ||
            (expectedNode.HasValue && response.SourceNodeId != expectedNode.Value))
            throw new InvalidDataException("Phản hồi không khớp.");
        return response;
    }

    private NetworkEnvelope DecodeTcpResponse(byte[] bytes)
    {
        try { return Authenticate(bytes); }
        catch (NetworkRejection ex) { throw new InvalidDataException(ex.Message, ex); }
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

public sealed class PeerTransport(WireProtocol wire, ChatStore store, FaultInjection faults, ClusterTls tls) : IPeerTransport, IClusterTransport
{
    private async Task<NetworkEnvelope> RequestAsync<T>(string address, int port, string type, T body, CancellationToken ct,
        NodeInfoDto? discovery = null, Guid? expectedNode = null, TimeSpan? attemptTimeout = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(attemptTimeout ?? TimeSpan.FromSeconds(type == "ControlProposal" ? 8 : 4));
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        if (discovery is not null) timeout.CancelAfter(250);
        await client.ConnectAsync(IPAddress.Parse(address), port, timeout.Token).ConfigureAwait(false);
        if (discovery is not null) timeout.CancelAfter(750);
        using var abort = timeout.Token.Register(() => client.Close());
        using var stream = await tls.ConnectAsync(client.GetStream(), timeout.Token);
        var source = discovery?.NodeId ?? (await store.GetIdentityAsync(timeout.Token)).NodeId;
        var requestId = LanChat.Domain.AppIdentity.NewId();
        await WireProtocol.WriteAsync(stream, wire.Encode(source, type, body, requestId), timeout.Token);
        var response = await wire.ReadTcpResponseAsync(stream, source, requestId, type + "Response", timeout.Token, expectedNode).ConfigureAwait(false);
        timeout.Token.ThrowIfCancellationRequested();
        return response;
    }
    public async Task<NodeInfoDto> GetNodeInfoAsync(string address, int port, CancellationToken cancellationToken)
    {
        return await DiscoverAsync(address, port, await store.GetNodeInfoAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }
    internal async Task<NodeInfoDto> DiscoverAsync(string address, int port, NodeInfoDto self, CancellationToken cancellationToken, Guid? expectedNode = null)
    {
        NetworkEnvelope response;
        try { response = await RequestAsync(address, port, "NodeInfo", self, cancellationToken, self, expectedNode).ConfigureAwait(false); }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
        { throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException ex) { throw new IOException("Hết thời gian xác minh máy trong LAN.", ex); }
        var info = WireProtocol.Payload<NodeInfoDto>(response);
        if (info.NodeId != response.SourceNodeId) throw new InvalidDataException("Sai định danh peer.");
        return info;
    }
    public async Task<T> CallAsync<T>(PeerEndpoint peer, string type, object payload, CancellationToken ct)
    {
        faults.ThrowIfBlocked(peer.NodeId);
        if (type == "ChatPush") throw new IOException("Giao thức 4 chuyển tin nhắn qua sự kiện đã mã hóa.");
        // These operations can safely retry after a lost acknowledgment.
        if (peer.NodeId == Guid.Empty || type is not ("Digest" or "EventRange" or "EventPush" or "ConsensusStatus" or "PairGroupCreate"))
            return await CallAtAddressAsync<T>(peer, type, payload, ct).ConfigureAwait(false);

        var preferred = store.GetPreferredPeerEndpoint(peer, ct);
        var (result, connected) = await CallWithFallbackAsync<T>(preferred, type, payload, ct).ConfigureAwait(false);
        await store.NotePeerConnectionAsync(peer.NodeId, ct, preferred, connected).ConfigureAwait(false);
        return result;
    }

    private async Task<(T Result, PeerEndpoint Connected)> CallWithFallbackAsync<T>(PeerEndpoint preferred, string type, object payload, CancellationToken ct)
    {
        try { return (await CallAtAddressAsync<T>(preferred, type, payload, ct).ConfigureAwait(false), preferred); }
        catch (Exception ex) when (IsRouteFailure(ex, ct))
        {
            foreach (var alternative in store.GetAlternatePeerEndpoints(preferred, ct))
            {
                try
                {
                    return (await CallAtAddressAsync<T>(alternative, type, payload, ct, TimeSpan.FromSeconds(2)).ConfigureAwait(false), alternative);
                }
                catch (Exception failure) when (IsRouteFailure(failure, ct)) { }
            }
            throw;
        }
    }

    private async Task<T> CallAtAddressAsync<T>(PeerEndpoint peer, string type, object payload, CancellationToken ct, TimeSpan? attemptTimeout = null)
    {
        var response = await RequestAsync(peer.Address, peer.Port, type, payload, ct,
            expectedNode: peer.NodeId != Guid.Empty ? peer.NodeId : null, attemptTimeout: attemptTimeout).ConfigureAwait(false);
        return WireProtocol.Payload<T>(response);
    }

    private static bool IsRouteFailure(Exception ex, CancellationToken ct) => !ct.IsCancellationRequested &&
        ex is IOException or InvalidDataException or SocketException or OperationCanceledException or ObjectDisposedException or
            FormatException or JsonException or System.Security.Authentication.AuthenticationException;
}

public sealed class NetworkHealth
{
    public string? Error { get; set; }
    private long _signatureRejected;
    private long _versionRejected;
    public string? DiscoveryIssue
    {
        get
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-30).Ticks;
            if (Interlocked.Read(ref _versionRejected) >= cutoff)
                return "Hai máy dùng giao thức LanChat khác nhau. Hãy cập nhật cùng bản cài.";
            if (Interlocked.Read(ref _signatureRejected) >= cutoff)
                return "Có máy cùng ID mạng nhưng chữ ký khác. Hãy kiểm tra mã mạng trên hai máy.";
            return null;
        }
    }

    internal void NoteRejection(NetworkRejection rejection, ILogger logger)
    {
        var now = DateTime.UtcNow.Ticks;
        ref var last = ref (rejection.Kind == NetworkRejectionKind.ProtocolVersion ? ref _versionRejected : ref _signatureRejected);
        var previous = Interlocked.Exchange(ref last, now);
        if (now - previous > TimeSpan.FromSeconds(30).Ticks)
            logger.LogWarning("LanChat authentication rejected: {Reason}", rejection.Message);
    }
}

public sealed class PeerConnectionManager(NodeOptions options, WireProtocol wire, ChatStore store,
    ILogger<PeerConnectionManager> logger, NetworkHealth health, IServiceProvider services, FaultInjection faults, ClusterTls tls) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var localNodeId = (await store.GetIdentityAsync(stoppingToken)).NodeId;
        var listener = new TcpListener(IPAddress.Any, options.ServicePort) { ExclusiveAddressUse = true };
        using var slots = new SemaphoreSlim(32);
        var clients = new List<Task>();
        try
        {
            listener.Start(); using var abort = stoppingToken.Register(listener.Stop);
            logger.LogInformation("TCP listener started on port {Port}, network {Cluster}, protocol v{Protocol}", options.ServicePort, options.ClusterId, NodeInfoDto.CurrentProtocol);
            while (!stoppingToken.IsCancellationRequested)
            {
                await slots.WaitAsync(stoppingToken);
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch { slots.Release(); throw; }
                clients.RemoveAll(x => x.IsCompleted);
                clients.Add(HandleAsync(client, slots, localNodeId, stoppingToken));
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
    private async Task HandleAsync(TcpClient client, SemaphoreSlim slots, Guid localNodeId, CancellationToken ct)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var abort = timeout.Token.Register(() => client.Close());
            try
            {
                using var stream = await tls.AcceptAsync(client.GetStream(), timeout.Token);
                var request = await wire.ReadTcpRequestAsync(stream, localNodeId, timeout.Token);
                faults.ThrowIfBlocked(request.SourceNodeId);
                var remoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                object response;
                switch (request.MessageType)
                {
                    case "NodeInfo":
                    case "Digest":
                        using (var body = JsonDocument.Parse(request.PayloadJson))
                            if (body.RootElement.ValueKind == JsonValueKind.Object && body.RootElement.TryGetProperty(nameof(NodeInfoDto.NodeId), out _))
                            {
                                var caller = WireProtocol.Payload<NodeInfoDto>(request);
                                if (caller.NodeId != request.SourceNodeId) throw new InvalidDataException("Sai định danh peer.");
                                await store.ObservePeerAsync(caller, remoteAddress, ChatStore.PeerObservation.Connection, timeout.Token);
                            }
                        response = request.MessageType == "NodeInfo" ? await store.GetNodeInfoAsync(timeout.Token) :
                            await services.GetRequiredService<IDistributedStore>().GetDigestAsync(timeout.Token);
                        break;
                    case "EventRange":
                        var range = WireProtocol.Payload<EventRangeRequest>(request);
                        var batch = await services.GetRequiredService<IDistributedStore>().GetEventRangeAsync(range, timeout.Token);
                        response = range.IncludeGroupHistory ? await store.AddGroupHistoryAsync(batch, request.SourceNodeId, timeout.Token) : batch;
                        break;
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
                    case "PairGroupCreate": response = await services.GetRequiredService<ConsensusService>().SavePairGroupAsync(WireProtocol.Payload<ControlCommand>(request), request.SourceNodeId, timeout.Token); break;
                    case "FileDownload":
                        timeout.CancelAfter(TimeSpan.FromMinutes(30));
                        var transfer = store.FindFileKey(WireProtocol.Payload<FileRequest>(request).TransferId, timeout.Token);
                        try
                        {
                            var files = services.GetRequiredService<PeerFileTransferService>();
                            using var input = transfer.HasValue ? await files.OpenVerifiedLocalAsync(transfer.Value.Hash, timeout.Token) : null;
                            var availability = new FileAvailability(input is not null, input?.Length ?? 0);
                            await WireProtocol.WriteAsync(stream, wire.Encode(localNodeId, "FileDownloadResponse", availability, request.RequestId), timeout.Token);
                            if (input is not null && transfer is { } file)
                                await FileTransferCrypto.WriteAsync(input, stream, input.Length, file.Hash, file.Key, timeout.Token);
                        }
                        finally { if (transfer is { } file) Array.Clear(file.Key, 0, file.Key.Length); }
                        return;
                    default: throw new InvalidDataException("Loại gói tin chưa hỗ trợ.");
                }
                await WireProtocol.WriteAsync(stream, wire.Encode(localNodeId, request.MessageType + "Response", response, request.RequestId), timeout.Token);
                if (request.MessageType is "Digest" or "EventPush" or "EventRange")
                    await store.NotePeerConnectionAsync(request.SourceNodeId, timeout.Token);
            }
            catch (NetworkRejection ex) { health.NoteRejection(ex, logger); }
            catch (InvalidDataException ex) { logger.LogDebug("Peer request rejected: {Reason}", ex.Message); }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or SocketException or JsonException or FormatException or OperationCanceledException or ArgumentException or NullReferenceException or System.Security.Authentication.AuthenticationException)
            { logger.LogDebug("Peer request rejected: {ErrorType}", ex.GetType().Name); }
            catch (Exception ex) { logger.LogError(ex, "Peer request failed"); }
            finally { slots.Release(); }
        }
    }
}
