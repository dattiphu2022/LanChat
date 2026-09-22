namespace LanChat.Domain;

public enum ReplicationEventType { UserProfileUpdated, MessageCreated, MessageEdited, MessageRecalled, ReactionChanged, ReceiptUpdated }
public enum OutboxStatus { Pending, Replicated }
public enum ControlCommandType { NoOp, JointMembership, FinalMembership, GroupCreated, GroupRenamed, GroupAvatarChanged, GroupMemberAdded, GroupMemberRemoved, GroupOwnerChanged, GroupDeleted }

public sealed class ReplicationEvent
{
    public Guid Id { get; set; }
    public Guid OriginNodeId { get; set; }
    public long OriginSequence { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid EntityId { get; set; }
    public Guid? ConversationId { get; set; }
    public ReplicationEventType Type { get; set; }
    public string PayloadJson { get; set; } = "";
    public long PhysicalTicks { get; set; }
    public int LogicalCounter { get; set; }
    public long CreatedAtTicks { get; set; }
    public string Signature { get; set; } = "";
}
public sealed class ReplicationCursor
{
    public Guid Id { get; set; }
    public Guid OriginNodeId { get; set; }
    public long LastOriginSequence { get; set; }
}
public sealed class OutboxItem
{
    public Guid Id { get; set; }
    public Guid ReplicationEventId { get; set; }
    public OutboxStatus Status { get; set; }
    public int RetryCount { get; set; }
    public long NextRetryTicks { get; set; }
    public string? LastError { get; set; }
}
public sealed class Group
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string Name { get; set; } = "";
    public Guid OwnerUserId { get; set; }
    public string? AvatarBase64 { get; set; }
    public bool IsDeleted { get; set; }
}
public sealed class GroupMember
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
}
public sealed class MessageReaction
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public string Emoji { get; set; } = "";
}
public sealed class MessageReceipt
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public bool IsRead { get; set; }
    public long ReceivedAtTicks { get; set; }
}
public sealed class Attachment
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public string FileName { get; set; } = "";
    public string MimeType { get; set; } = "application/octet-stream";
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = "";
}
public sealed class ClusterState
{
    public Guid Id { get; set; }
    public Guid ClusterId { get; set; }
    public long CurrentTerm { get; set; }
    public Guid? VotedForNodeId { get; set; }
    public Guid? CurrentLeaderNodeId { get; set; }
    public long CommitIndex { get; set; }
    public long LastAppliedIndex { get; set; }
}
public sealed class ControlLogEntry
{
    public Guid Id { get; set; }
    public long Index { get; set; }
    public long Term { get; set; }
    public string CommandJson { get; set; } = "";
}
