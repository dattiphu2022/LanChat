using LanChat.Domain;

namespace LanChat.Application;

public sealed class NodeOptions
{
    public string DataDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanChat", "Data");
    public const int DefaultServicePort = 54546;
    public int ServicePort { get; set; } = DefaultServicePort;
    public int DiscoveryPort { get; set; } = 54545;
    public bool OverrideServicePort { get; set; }
    public bool OverrideDiscoveryPort { get; set; }
    public Guid ClusterId { get; set; }
    public byte[] ClusterSecret { get; set; } = [];
    public List<string> DiscoveryTargets { get; set; } = [];
    public string? InitialDepartment { get; set; } = "Tên Đơn Vị";
    public string InitialDisplayName { get; set; } = "Tên Người Dùng";
    public Guid BootstrapNodeId { get; set; }
    public Guid? InitialNodeId { get; set; }
    public byte[] TlsCertificatePfx { get; set; } = [];
    public TimeSpan LeaderTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(25);
}

public sealed record UserProfileDto(Guid Id, string DisplayName, string? FullName, string? Nickname,
    string? Department, string? Position, string? Phone, string? StatusMessage);
public sealed record DiscoveredPeerDto(Guid NodeId, Guid UserId, string DisplayName, string DeviceName,
    string IPAddress, int ServicePort, NodeRole CurrentRole, long Term, int ProtocolVersion,
    string ApplicationVersion, Guid ClusterId);
public sealed record DevicePublicKeys(string EncryptionKey, string SigningKey);
public sealed record EncryptionIdentity(Guid UserId, string DisplayName, string Fingerprint, bool IsLocal)
{
    public string? Department { get; init; }
}
public sealed record NodeInfoDto(Guid NodeId, string DeviceName, int ServicePort, UserProfileDto User,
    int ProtocolVersion = NodeInfoDto.CurrentProtocol, string ApplicationVersion = NodeInfoDto.CurrentApplicationVersion, NodeRole Role = NodeRole.Follower, long Term = 0, bool CanLead = true,
    DevicePublicKeys? DeviceKeys = null, string? KeyProof = null)
{
    public const int CurrentProtocol = 4;
    public const string CurrentApplicationVersion = "1.2.8";
}
public sealed record ChatMessageDto(Guid Id, Guid ClientMessageId, Guid ConversationId, Guid SenderUserId,
    Guid RecipientUserId, Guid OriginNodeId, long OriginSequence, MessageKind Kind, string Text,
    long CreatedAtTicks, long PhysicalTicks, int LogicalCounter);
public sealed record UserSummary(Guid UserId, string DisplayName, string? Department, string DeviceName,
    string IPAddress, int ServicePort, Guid NodeId, long LastSeenTicks, PresenceStatus Presence);
public sealed record ConversationSummary(Guid Id, Guid OtherUserId, string DisplayName, string Preview,
    long LastMessageTicks, int UnreadCount, bool IsPinned, bool IsMuted, PresenceStatus Presence, ConversationKind Kind = ConversationKind.Direct, string? AvatarBase64 = null)
{
    public string? Department { get; init; }
    public int UnreadMentionCount { get; init; }
}
public sealed record IncomingMessageNotice(Guid ConversationId, string Preview)
{
    public Guid MessageId { get; init; }
    public string SenderName { get; init; } = "";
    public bool Mentioned { get; init; }
}
public sealed record IncomingMessageBatch(long Position, IncomingMessageNotice[] Messages);
public sealed record NetworkInfo(string Adapter, string Address, string Mask, int PrefixLength,
    string Network, string Broadcast, string FirstHost, string LastHost, long HostCount, string? Gateway);
public sealed record DiagnosticsSnapshot(NodeIdentity Identity, int OnlinePeers, int PendingMessages,
    IReadOnlyList<UserSummary> Users, IReadOnlyList<NetworkInfo> Networks, string? NetworkError);

public sealed record LanScanProgress(int Checked, int Discovered, bool CheckingUdp, int? Total = null);

public interface ILanScanner
{
    Task<IReadOnlyList<DiscoveredPeerDto>> ScanAsync(CancellationToken cancellationToken = default, IProgress<LanScanProgress>? progress = null);
}
public interface ILocalNetworkInfoProvider { IReadOnlyList<NetworkInfo> GetNetworks(); }
public interface IPeerTransport
{
    Task<NodeInfoDto> GetNodeInfoAsync(string address, int port, CancellationToken cancellationToken);
}
public interface IChatStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<NodeIdentity> GetIdentityAsync(CancellationToken ct = default);
    Task<LocalUserProfile> GetProfileAsync(CancellationToken ct = default);
    Task SaveProfileAsync(LocalUserProfile profile, CancellationToken ct = default);
    Task<NodeInfoDto> GetNodeInfoAsync(CancellationToken ct = default);
    Task<EncryptionIdentity[]> GetEncryptionIdentitiesAsync(CancellationToken ct = default);
    Task RememberPeerAsync(NodeInfoDto peer, string address, CancellationToken ct = default);
    Task<IReadOnlyList<UserSummary>> GetUsersAsync(string search = "", CancellationToken ct = default);
    Task<Guid> OpenDirectAsync(Guid userId, CancellationToken ct = default);
    Task ShowConversationAsync(Guid id, CancellationToken ct = default);
    Task DeleteConversationLocalAsync(Guid id, CancellationToken ct = default);
    Task<IncomingMessageBatch> GetIncomingMessagesAsync(long? after, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(string search = "", CancellationToken ct = default);
    Task<IReadOnlyList<Message>> GetMessagesAsync(Guid conversationId, long? beforeTicks = null, CancellationToken ct = default, int limit = 200);
    long Revision { get; }
    long PresenceRevision { get; }
    Task<Message[]> GetOlderMessagesAsync(Guid conversationId, Message before, int limit, CancellationToken ct = default);
    Task<Message> SaveOutgoingAsync(Guid recipientId, string text, CancellationToken ct = default);
    Task SetConversationAsync(Guid id, bool? pin = null, bool? mute = null, bool markRead = false, CancellationToken ct = default);
    Task<int> GetPendingCountAsync(CancellationToken ct = default);
}
public interface IChatService
{
    Task<Message> SendAsync(Guid recipientId, string text, CancellationToken ct = default);
    Task<ForwardResult[]> ForwardBatchAsync(ForwardRequest[] requests, IProgress<ForwardProgress>? progress = null, CancellationToken ct = default);
    Task<Message> ForwardAsync(Guid messageId, Guid conversationId, IProgress<FileProgress>? progress = null, CancellationToken ct = default);
}
public sealed partial class ChatService(IChatStore store, IDistributedStore distributed, IFileTransferService files) : IChatService
{
    public Task<Message> SendAsync(Guid recipientId, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16000)
            throw new ArgumentException("Tin nhắn cần có nội dung và không quá 16.000 ký tự.");
        return store.SaveOutgoingAsync(recipientId, text.Trim(), ct);
    }

    public async Task<Message> ForwardAsync(Guid messageId, Guid conversationId, IProgress<FileProgress>? progress = null, CancellationToken ct = default)
    {
        var destination = (await store.GetConversationsAsync(ct: ct).ConfigureAwait(false)).FirstOrDefault(c => c.Id == conversationId)
            ?? throw new InvalidOperationException("Hội thoại nhận chưa sẵn sàng.");
        var result = (await ForwardBatchAsync([new(messageId, new(conversationId, true, destination.DisplayName))],
            progress is null ? null : new SingleForwardProgress(progress), ct).ConfigureAwait(false)).Single();
        if (result.Outcome == ForwardOutcome.Cancelled) throw new OperationCanceledException(ct);
        if (result.Cause is { } cause) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();
        if (result.CreatedMessageId is not Guid id) throw new InvalidOperationException(result.Error);
        return (await distributed.GetMessageDetailsAsync(id, ct).ConfigureAwait(false)).Message;
    }
    private sealed class SingleForwardProgress(IProgress<FileProgress> progress) : IProgress<ForwardProgress>
    { public void Report(ForwardProgress value) { if (value.File is { } file) progress.Report(file); } }
}
