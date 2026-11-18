using LanChat.Domain;

namespace LanChat.Application;

public sealed record EventDto(Guid Id, Guid OriginNodeId, long OriginSequence, Guid ActorUserId, Guid EntityId,
    Guid? ConversationId, ReplicationEventType Type, string PayloadJson, long PhysicalTicks, int LogicalCounter, long CreatedAtTicks, string Signature,
    DevicePublicKeys? DeviceKeys = null);
public sealed record MessageContent(ChatMessageDto Message, UserProfileDto Sender, UserProfileDto? Recipient,
    Guid? ReplyToMessageId = null, Guid? ForwardedFromMessageId = null, AttachmentDto[]? Attachments = null, Guid[]? Audience = null);
public sealed record MessageEdit(string Text);
public sealed record ReactionChange(string Emoji, bool Active);
public sealed record ReceiptChange(bool IsRead, long ReceivedAtTicks);
public sealed record AttachmentDto(Guid Id, Guid MessageId, string FileName, string MimeType, long FileSize, string Sha256, string? FileKey = null);
public sealed record FolderFile(string RelativePath, long FileSize, string Sha256, string? FileKey = null);
public sealed record FolderManifest(string Name, string[] Directories, FolderFile[] Files);
public static class FolderAttachment
{
    public const string MimeType = "application/vnd.lanchat.folder+json";
}
public sealed record PeerEndpoint(Guid NodeId, string Address, int Port);
public sealed record CursorDto(Guid OriginNodeId, long LastOriginSequence);
public sealed record ReplicationDigest(CursorDto[] Cursors)
{
    public bool SupportsGroupHistory { get; init; }
    public string? GroupHistoryRevision { get; init; }
}
public sealed record EventRangeRequest(Guid OriginNodeId, long AfterSequence, int Limit = 100)
{
    public bool IncludeGroupHistory { get; init; }
}
public sealed record EventBatch(EventDto[] Events)
{
    public GroupHistoryKey[] GroupHistoryKeys { get; init; } = [];
}
public sealed record GroupHistoryKey(Guid EventId, Guid GroupId, Guid GrantorNodeId, Guid GrantorUserId,
    Guid RecipientUserId, string WrappedKey, DevicePublicKeys DeviceKeys, string? Signature = null);
public sealed record EventAck(Guid[] EventIds);
public sealed record GroupSummary(Guid Id, string Name, Guid OwnerUserId, Guid[] Members, bool IsDeleted, string? AvatarBase64)
{
    public bool PendingCreation { get; init; }
}
public sealed record MessageDetails(Message Message, AttachmentDto[] Attachments, string[] Reactions, int ReceivedCount, int ReadCount, string? ReplyPreview, string? SenderName = null)
{
    public string? SenderDepartment { get; init; }
    public ReplyQuote? Quote { get; init; }
}
public sealed record ReplyQuote(Guid MessageId, string Preview, string? SenderName = null, string? SenderDepartment = null, bool CanNavigate = false);
public sealed record SearchResult(Guid MessageId, Guid ConversationId, string Text, long CreatedAtTicks);
public sealed record DistributedDiagnostics(int EventCount, int OutboxPending, CursorDto[] Cursors, long LastSyncTicks);
public sealed record PeerDiagnostic(Guid NodeId, string DeviceName, string Address, NodeRole Role, long Term, long LastSeenTicks);

public sealed record ControlCommand(Guid Id, ControlCommandType Type, Guid ActorUserId, Guid GroupId,
    string? Name = null, Guid? TargetUserId = null, Guid[]? Members = null, Guid[]? OldVoters = null,
    Guid[]? NewVoters = null, string? AvatarBase64 = null, long CreatedAtTicks = 0,
    Guid OriginNodeId = default, Guid? PreviousCommandId = null, DevicePublicKeys? DeviceKeys = null, string? Signature = null);
public sealed record LogEntryDto(Guid Id, long Index, long Term, ControlCommand Command);
public sealed record VoteRequest(long Term, Guid CandidateNodeId, long LastLogIndex, long LastLogTerm, Guid? TransferFrom = null);
public sealed record VoteResponse(long Term, bool Granted);
public sealed record AppendRequest(long Term, Guid LeaderNodeId, long PreviousIndex, long PreviousTerm, LogEntryDto[] Entries, long LeaderCommit, bool LeaderReady = false);
public sealed record AppendResponse(long Term, bool Success, long MatchIndex);
public sealed record ControlResult(bool Success, string? Error, long Index = 0, Guid? LeaderNodeId = null);
public sealed record ConsensusStatus(NodeRole Role, long Term, Guid? LeaderNodeId, bool HasQuorum,
    Guid[] Voters, Guid[]? JointVoters, long CommitIndex, long LastLeaderHeartbeatTicks, string StatusText, bool CanLead = true, bool LeaderReady = false)
{
    public bool SupportsPairGroups { get; init; }
    public Guid? SoleOnlinePeerNodeId { get; init; }
    public Guid? PairCoordinatorNodeId { get; init; }
    public Guid? PairGroupMemberUserId { get; init; }
}
public sealed record LeadershipResult(bool Success, string? Error);
public sealed record LeadershipHandoff(long Term, long LastIndex, long LastTerm);

public interface IClusterTransport
{
    Task<T> CallAsync<T>(PeerEndpoint peer, string type, object payload, CancellationToken ct);
}
public interface IDistributedStore
{
    Task<ReplicationDigest> GetDigestAsync(CancellationToken ct = default);
    Task<EventBatch> GetEventRangeAsync(EventRangeRequest request, CancellationToken ct = default);
    Task<EventAck> ImportEventsAsync(EventBatch batch, CancellationToken ct = default);
    Task<PeerEndpoint[]> GetPeerEndpointsAsync(CancellationToken ct = default);
    Task<EventDto[]> GetOutboxAsync(CancellationToken ct = default);
    Task RecordOutboxResultAsync(Guid[] eventIds, bool success, CancellationToken ct = default);
    Task<DistributedDiagnostics> GetDistributedDiagnosticsAsync(CancellationToken ct = default);
    Task<PeerDiagnostic[]> GetPeerDiagnosticsAsync(CancellationToken ct = default);
    Task<Message> SendToConversationAsync(Guid conversationId, string text, Guid? replyTo = null,
        Guid? forwardedFrom = null, AttachmentDto[]? attachments = null, CancellationToken ct = default);
    Task EditMessageAsync(Guid messageId, string text, CancellationToken ct = default);
    Task RecallMessageAsync(Guid messageId, CancellationToken ct = default);
    Task DeleteLocalAsync(Guid messageId, CancellationToken ct = default);
    Task ReactAsync(Guid messageId, string emoji, bool active, CancellationToken ct = default);
    Task<MessageDetails> GetMessageDetailsAsync(Guid messageId, CancellationToken ct = default);
    Task<MessageDetails[]> GetMessageDetailsBatchAsync(Guid[] messageIds, CancellationToken ct = default);
    Task<Message[]> GetMessageWindowAsync(Guid conversationId, Guid anchorId, CancellationToken ct = default);
    Task<SearchResult[]> SearchMessagesAsync(string text, Guid? conversationId = null, CancellationToken ct = default);
    Task<GroupSummary[]> GetGroupsAsync(CancellationToken ct = default);
    Task<AttachmentDto[]> GetAttachmentsAsync(Guid? conversationId = null, CancellationToken ct = default);
}
public interface IConsensusService
{
    Task<ConsensusStatus> GetStatusAsync(CancellationToken ct = default);
    Task<LeadershipResult> CampaignAsync(CancellationToken ct = default);
    Task<LeadershipResult> NominateAsync(Guid nodeId, CancellationToken ct = default);
    Task<LeadershipResult> SetCanLeadAsync(bool enabled, CancellationToken ct = default);
    Task<ControlResult> ProposeAsync(ControlCommand command, CancellationToken ct = default);
}
public sealed record FileProgress(long Completed, long Total);
public sealed record FileRequest(string TransferId);
public sealed record FileAvailability(bool Available, long Length);
public interface IFileTransferService
{
    Task<AttachmentDto> ImportLocalAsync(string path, CancellationToken ct = default);
    Task<AttachmentDto> ImportFolderAsync(string path, IProgress<string>? progress = null, CancellationToken ct = default);
    Task<string?> GetLocalPathAsync(string sha256, CancellationToken ct = default);
    Task<string> DownloadAsync(AttachmentDto file, IProgress<FileProgress>? progress = null, CancellationToken ct = default);
    Task<string> PrepareOpenAsync(AttachmentDto file, IProgress<FileProgress>? progress = null, CancellationToken ct = default);
    Task<AttachmentDto> PrepareForwardAsync(AttachmentDto file, IProgress<FileProgress>? progress = null, CancellationToken ct = default);
}

public sealed class FaultInjection
{
    public bool PauseReplication { get; set; }
    public bool StopHeartbeat { get; set; }
    public bool SimulateLeaderFailure { get; set; }
    public HashSet<Guid> BlockedNodes { get; } = [];
    public DateTimeOffset LastSync { get; set; }
    public void ThrowIfBlocked(Guid node)
    {
        lock (BlockedNodes) if (BlockedNodes.Contains(node)) throw new IOException("Kết nối bị ngắt bởi mô phỏng lỗi.");
    }
}

public sealed class NodeRuntimeState
{
    private volatile NodeRole _role;
    public NodeRole Role { get => _role; set => _role = value; }
    public volatile bool CanLead = true;
    private readonly object _leaderGate = new();
    private (Guid? Node, long Term) _leader;
    public event Action<Guid, long>? LeaderReady;
    public void SetReadyLeader(Guid? node, long term)
    {
        lock (_leaderGate)
        {
            if (_leader == (node, term)) return;
            _leader = (node, term);
            if (node.HasValue) LeaderReady?.Invoke(node.Value, term);
        }
    }
}
