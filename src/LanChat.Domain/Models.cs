using System.Security.Cryptography;
using System.Text;

namespace LanChat.Domain;

public enum PresenceStatus { Unknown, Online, Away, Offline }
public enum NodeRole { Follower, Candidate, Leader }
public enum MessageKind { Text, Image, File, System, Audio, Video, Poll, Reminder, Location }
public enum MessageStatus { LocalPending, Synced, Delivered }
public enum ConversationKind { Direct, Group }

public sealed class LocalUserProfile
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string? FullName { get; set; }
    public string? Nickname { get; set; }
    public string? Department { get; set; }
    public string? Position { get; set; }
    public string? Phone { get; set; }
    public string? StatusMessage { get; set; }
    public string? AvatarPath { get; set; }
}

public sealed class NodeIdentity
{
    public Guid NodeId { get; set; }
    public Guid UserId { get; set; }
    public string DeviceName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PeerNode
{
    public Guid Id { get; set; }
    public Guid NodeId { get; set; }
    public Guid UserId { get; set; }
    public string DeviceName { get; set; } = "";
    public string IPAddress { get; set; } = "";
    public int ServicePort { get; set; }
    public long LastSeenTicks { get; set; }
    public NodeRole Role { get; set; }
    public long Term { get; set; }
    public string ApplicationVersion { get; set; } = "";
}

public sealed class Conversation
{
    public Guid Id { get; set; }
    public ConversationKind Kind { get; set; }
    public bool IsPinned { get; set; }
    public bool IsMuted { get; set; }
    public bool IsHiddenLocal { get; set; }
    public long LastReadTicks { get; set; }
    public string Title { get; set; } = "";
}

public sealed class ConversationMember
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
}

public sealed class Message
{
    public Guid Id { get; set; }
    public Guid ClientMessageId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderUserId { get; set; }
    public Guid RecipientUserId { get; set; }
    public Guid OriginNodeId { get; set; }
    public long OriginSequence { get; set; }
    public MessageKind Kind { get; set; }
    public string Text { get; set; } = "";
    public long CreatedAtTicks { get; set; }
    public long PhysicalTicks { get; set; }
    public int LogicalCounter { get; set; }
    public MessageStatus Status { get; set; }
    public bool IsRead { get; set; }
    public int RetryCount { get; set; }
    public long NextRetryTicks { get; set; }
    public string? LastError { get; set; }
    public Guid? ReplyToMessageId { get; set; }
    public Guid? ForwardedFromMessageId { get; set; }
    public bool IsEdited { get; set; }
    public bool IsRecalled { get; set; }
    public bool IsDeletedLocal { get; set; }
}

public sealed class LocalSettings
{
    public Guid Id { get; set; }
    public Guid NodeId { get; set; }
    public Guid UserId { get; set; }
    public long CreatedAtTicks { get; set; }
    public long LastSequence { get; set; }
    public long PhysicalTicks { get; set; }
    public int LogicalCounter { get; set; }
    public long EventSequence { get; set; }
    public Guid ClusterId { get; set; }
}

public static class DirectConversation
{
    public static Guid GetId(Guid clusterId, Guid first, Guid second)
    {
        var users = new[] { first.ToString("N"), second.ToString("N") };
        Array.Sort(users, StringComparer.Ordinal);
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(Encoding.UTF8.GetBytes($"{clusterId:N}:{users[0]}:{users[1]}"));
        Array.Resize(ref digest, 16);
        return new Guid(digest);
    }
}

public static class HybridClock
{
    public static (long Physical, int Logical) Advance(long local, int counter, long remote = 0, int remoteCounter = 0)
    {
        var physical = Math.Max(DateTime.UtcNow.Ticks, Math.Max(local, remote));
        var logical = physical == local && physical == remote ? Math.Max(counter, remoteCounter) + 1
            : physical == local ? counter + 1 : physical == remote ? remoteCounter + 1 : 0;
        return (physical, logical);
    }
}
