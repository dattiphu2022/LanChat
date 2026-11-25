using System.Security.Cryptography;
using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;

namespace LanChat.Infrastructure;

public sealed partial class ChatStore
{
    private const int MaxEventPayloadChars = 256 * 1024;
    internal static UserProfileDto ToProfile(LocalUserProfile p) => new(p.Id, p.DisplayName, p.FullName, p.Nickname, p.Department, p.Position, p.Phone, p.StatusMessage);
    internal static EventDto ToEvent(ReplicationEvent e) => new(e.Id, e.OriginNodeId, e.OriginSequence, e.ActorUserId, e.EntityId, e.ConversationId,
        e.Type, e.PayloadJson, e.PhysicalTicks, e.LogicalCounter, e.CreatedAtTicks, e.Signature, new(e.EncryptionKey, e.SigningKey));
    private async Task InitializeDistributedAsync(CancellationToken ct)
    {
        await WriteAsync(async db =>
        {
            var s = db.Required<LocalSettings>();
            if (s.ClusterId != Guid.Empty && s.ClusterId != options.ClusterId) throw new InvalidOperationException("Database thuộc mạng chat khác. Hãy dùng thư mục dữ liệu riêng.");
            s.ClusterId = options.ClusterId;
            if (!db.Exists<ClusterState>()) db.Insert(new ClusterState { Id = LanChat.Domain.AppIdentity.NewId(), ClusterId = options.ClusterId });
            if (!db.Exists<ReplicationEvent>("OriginNodeId=$0 AND Type=$1", s.NodeId, ReplicationEventType.UserProfileUpdated))
                await EmitAsync(db, s, ReplicationEventType.UserProfileUpdated, s.UserId, null, ToProfile(db.Required<LocalUserProfile>("Id=$0", s.UserId)), ct);
            return true;
        }, ct);
    }
    private async Task<EventDto> EmitAsync<T>(ChatDbContext db, LocalSettings s, ReplicationEventType type, Guid entityId,
        Guid? conversationId, T payload, CancellationToken ct, Guid[]? audience = null)
    {
        (s.PhysicalTicks, s.LogicalCounter) = HybridClock.Advance(s.PhysicalTicks, s.LogicalCounter);
        var e = new ReplicationEvent { Id = LanChat.Domain.AppIdentity.NewId(), OriginNodeId = s.NodeId, OriginSequence = ++s.EventSequence,
            ActorUserId = s.UserId, EntityId = entityId, ConversationId = conversationId, Type = type,
            EncryptionKey = Device.PublicKeys.EncryptionKey, SigningKey = Device.PublicKeys.SigningKey,
            PhysicalTicks = s.PhysicalTicks, LogicalCounter = s.LogicalCounter, CreatedAtTicks = DateTime.UtcNow.Ticks };
        e.PayloadJson = type == ReplicationEventType.UserProfileUpdated ? JsonSerializer.Serialize(payload) :
            EncryptEvent(db, e.Id, payload, audience ?? ActiveMessageAudience(db, entityId, conversationId!.Value));
        if (e.PayloadJson.Length > MaxEventPayloadChars) throw new ArgumentException("Nội dung đồng bộ vượt giới hạn.");
        e.Signature = Device.Sign(options.ClusterId, "event", ToEvent(e));
        db.Insert(e); db.Insert(new OutboxItem { Id = LanChat.Domain.AppIdentity.NewId(), ReplicationEventId = e.Id });
        var cursor = db.One<ReplicationCursor>("OriginNodeId=$0", s.NodeId);
        if (cursor is null) { cursor = new ReplicationCursor { Id = LanChat.Domain.AppIdentity.NewId(), OriginNodeId = s.NodeId }; db.Insert(cursor); }
        cursor.LastOriginSequence = s.EventSequence;
        db.SaveChanges();
        await ProjectEntityAsync(db, e.EntityId, ct);
        return ToEvent(e);
    }
    private async Task EmitMessageAsync(ChatDbContext db, LocalSettings s, Message message, AttachmentDto[]? attachments, CancellationToken ct, MessageMention[]? mentions = null)
    {
        var sender = db.Required<LocalUserProfile>("Id=$0", message.SenderUserId);
        var recipient = db.One<LocalUserProfile>("Id=$0", message.RecipientUserId);
        var audience = db.Rows<ConversationMember>("ConversationId=$0", message.ConversationId).Select(x => x.UserId).ToArray();
        var dto = new ChatMessageDto(message.Id, message.ClientMessageId, message.ConversationId, message.SenderUserId, message.RecipientUserId,
            message.OriginNodeId, message.OriginSequence, message.Kind, message.Text, message.CreatedAtTicks, message.PhysicalTicks, message.LogicalCounter);
        var content = new MessageContent(dto, ToProfile(sender), recipient is null ? null : ToProfile(recipient),
            message.ReplyToMessageId, message.ForwardedFromMessageId, attachments, audience, mentions);
        await EmitAsync(db, s, ReplicationEventType.MessageCreated, message.Id, message.ConversationId, content, ct, audience);
    }
    public async Task<ReplicationDigest> GetDigestAsync(CancellationToken ct = default)
    {
        using var db = database.Open(ct);
        return new(db.Rows<ReplicationCursor>().Select(x => new CursorDto(x.OriginNodeId, x.LastOriginSequence)).ToArray())
        { SupportsGroupHistory = true, GroupHistoryRevision = GroupHistoryRevision(db) };
    }
    public async Task<EventBatch> GetEventRangeAsync(EventRangeRequest request, CancellationToken ct = default)
    {
        using var db = database.Open(ct);
        var rows = db.Rows<ReplicationEvent>("OriginNodeId=$0 AND OriginSequence>$1 ORDER BY OriginSequence LIMIT $2", request.OriginNodeId, request.AfterSequence, Math.Min(100, Math.Max(1, request.Limit)));
        // Bound by serialized byte size as well as row count; a batch can contain large text/metadata events.
        var result = new List<EventDto>(); var bytes = 0;
        foreach (var row in rows)
        {
            var e = ToEvent(row); var length = JsonSerializer.SerializeToUtf8Bytes(e).Length;
            if (result.Count > 0 && bytes + length > 500_000) break;
            result.Add(e); bytes += length;
        }
        return new(result.ToArray());
    }
    public Task<EventAck> ImportEventsAsync(EventBatch batch, CancellationToken ct = default) => WriteAsync(async db =>
    {
        if (batch.Events.Length > 100 || batch.GroupHistoryKeys is null || batch.GroupHistoryKeys.Length > 100)
            throw new InvalidDataException("Lô sự kiện quá lớn.");
        var s = db.Required<LocalSettings>();
        var changed = new HashSet<Guid>(); var origins = new HashSet<Guid>();
        foreach (var e in batch.Events)
        {
            var old = db.One<ReplicationEvent>("Id=$0 OR (OriginNodeId=$1 AND OriginSequence=$2)", e.Id, e.OriginNodeId, e.OriginSequence);
            if (old is not null)
            {
                if (ToEvent(old) != e) throw new InvalidDataException("Xung đột sự kiện bất biến.");
                if (e.ConversationId.HasValue && db.Exists<Group>("Id=$0", e.ConversationId)) changed.Add(e.EntityId);
                continue;
            }
            if (e.OriginNodeId == s.NodeId) throw new InvalidDataException("Không nhận sự kiện mới giả danh node cục bộ.");
            ValidateEvent(db, e);
            db.Insert(new ReplicationEvent { Id = e.Id, OriginNodeId = e.OriginNodeId, OriginSequence = e.OriginSequence,
                ActorUserId = e.ActorUserId, EntityId = e.EntityId, ConversationId = e.ConversationId, Type = e.Type, PayloadJson = e.PayloadJson,
                PhysicalTicks = e.PhysicalTicks, LogicalCounter = e.LogicalCounter, CreatedAtTicks = e.CreatedAtTicks, Signature = e.Signature,
                EncryptionKey = e.DeviceKeys!.EncryptionKey, SigningKey = e.DeviceKeys.SigningKey });
            (s.PhysicalTicks, s.LogicalCounter) = HybridClock.Advance(s.PhysicalTicks, s.LogicalCounter, e.PhysicalTicks, e.LogicalCounter);
            changed.Add(e.EntityId); origins.Add(e.OriginNodeId);
            db.SaveChanges();
        }
        foreach (var origin in origins)
        {
            var cursor = db.One<ReplicationCursor>("OriginNodeId=$0", origin);
            if (cursor is null) { cursor = new ReplicationCursor { Id = LanChat.Domain.AppIdentity.NewId(), OriginNodeId = origin }; db.Insert(cursor); }
            // Never skip a gap, even when a later event arrived through realtime delivery.
            while (true)
            {
                var sequences = db.Rows<ReplicationEvent>("OriginNodeId=$0 AND OriginSequence>$1 ORDER BY OriginSequence LIMIT 500", origin, cursor.LastOriginSequence).Select(x => x.OriginSequence).ToArray();
                var start = cursor.LastOriginSequence;
                foreach (var sequence in sequences) { if (sequence != cursor.LastOriginSequence + 1) break; cursor.LastOriginSequence = sequence; }
                if (sequences.Length < 500 || cursor.LastOriginSequence == start || cursor.LastOriginSequence != sequences[sequences.Length - 1]) break;
            }
        }
        ImportGroupHistoryKeys(db, batch.GroupHistoryKeys, changed);
        foreach (var id in changed) await ProjectEntityAsync(db, id, ct);
        db.SaveChanges();
        foreach (var id in changed)
        {
            var m = db.One<Message>("Id=$0", id);
            if (m is not null && m.SenderUserId != s.UserId && db.Exists<ConversationMember>("ConversationId=$0 AND UserId=$1", m.ConversationId, s.UserId)
                && !db.Exists<MessageReceipt>("MessageId=$0 AND UserId=$1", id, s.UserId))
                await EmitAsync(db, s, ReplicationEventType.ReceiptUpdated, id, m.ConversationId, new ReceiptChange(false, DateTime.UtcNow.Ticks), ct);
        }
        return new EventAck(batch.Events.Select(x => x.Id).ToArray());
    }, ct);
    private void ValidateEvent(ChatDbContext db, EventDto e)
    {
        if (e.Id == Guid.Empty || e.EntityId == Guid.Empty || e.ActorUserId == Guid.Empty || e.OriginNodeId == Guid.Empty || e.OriginSequence < 1 ||
            e.PayloadJson is null || e.PayloadJson.Length > MaxEventPayloadChars || e.PhysicalTicks < 1 || e.PhysicalTicks > DateTime.UtcNow.AddDays(1).Ticks || e.LogicalCounter < 0 || e.LogicalCounter > 10000000 ||
            !Enum.IsDefined(typeof(ReplicationEventType), e.Type))
            throw new InvalidDataException("Sự kiện không hợp lệ.");
        DeviceIdentity.Verify(options.ClusterId, "event", e with { Signature = "" }, e.DeviceKeys, e.Signature);
        PinDevice(db, e.OriginNodeId, e.ActorUserId, e.DeviceKeys!);
        if (e.Type == ReplicationEventType.UserProfileUpdated)
        {
            if (e.ActorUserId != e.EntityId || e.ConversationId is not null) throw new InvalidDataException("Định danh hồ sơ không hợp lệ.");
        }
        else
        {
            if (e.ConversationId is null || e.ConversationId == Guid.Empty) throw new InvalidDataException("Thiếu định danh cuộc trò chuyện.");
            _ = DeviceIdentity.ParseEnvelope(e.PayloadJson);
        }
    }
    public async Task<PeerEndpoint[]> GetPeerEndpointsAsync(CancellationToken ct = default)
    {
        using var db = database.Open(ct);
        return ReadPeerEndpoints(db, "1");
    }
    internal long NextOutboxRetryTicks(CancellationToken ct)
    {
        using var db = database.Open(ct);
        return db.Scalar("SELECT COALESCE(MIN(NextRetryTicks),$1) FROM OutboxItems WHERE Status=$0",
            OutboxStatus.Pending, long.MaxValue);
    }

    public async Task<EventDto[]> GetOutboxAsync(CancellationToken ct = default)
    {
        using var db = database.Open(ct); var now = DateTime.UtcNow.Ticks;
        // Importing history can enqueue a receipt for every message at once.
        // Keep those receipts from occupying the whole batch ahead of user activity.
        var events = db.Rows<ReplicationEvent>(
            "Id IN (SELECT pending.ReplicationEventId FROM OutboxItems pending " +
            "JOIN ReplicationEvents queued ON queued.Id=pending.ReplicationEventId " +
            "WHERE pending.Status=$0 AND pending.NextRetryTicks<=$1 " +
            "ORDER BY CASE WHEN queued.Type=$2 THEN 1 ELSE 0 END, pending.NextRetryTicks, queued.OriginSequence LIMIT 20) " +
            "ORDER BY CASE WHEN Type=$2 THEN 1 ELSE 0 END, OriginSequence",
            OutboxStatus.Pending, now, ReplicationEventType.ReceiptUpdated);
        var result = new List<EventDto>(); var bytes = 0;
        foreach (var row in events)
        {
            var item = ToEvent(row); var length = JsonSerializer.SerializeToUtf8Bytes(item).Length;
            if (result.Count > 0 && bytes + length > 500_000) break;
            result.Add(item); bytes += length;
        }
        return result.OrderBy(item => item.OriginSequence).ToArray();
    }
    public async Task RecordOutboxResultAsync(Guid[] eventIds, bool success, CancellationToken ct = default)
    {
        await WriteAsync(async db =>
        {
            var rows = db.Rows<OutboxItem>(In("ReplicationEventId", eventIds), Args(eventIds));
            foreach (var row in rows)
            {
                if (success) { row.Status = OutboxStatus.Replicated; row.LastError = null; }
                else if (row.Status == OutboxStatus.Pending) { row.RetryCount++; row.NextRetryTicks = DateTime.UtcNow.AddSeconds(Math.Min(30, Math.Pow(2, Math.Min(5, row.RetryCount)))).Ticks; row.LastError = "Chưa kết nối được peer."; }
            }
            if (success)
            {
                foreach (var e in db.Rows<ReplicationEvent>(In("Id", eventIds), Args(eventIds)).Where(e => e.Type == ReplicationEventType.MessageCreated))
                    if (db.Execute("UPDATE Messages SET Status=$0 WHERE Id=$1 AND Status=$2", MessageStatus.Synced, e.EntityId, MessageStatus.LocalPending) > 0)
                        db.NoteContentChange();
            }
            return true;
        }, ct);
    }
    public async Task<DistributedDiagnostics> GetDistributedDiagnosticsAsync(CancellationToken ct = default)
    {
        using var db = database.Open(ct);
        return new((int)db.Count<ReplicationEvent>(), (int)db.Count<OutboxItem>("Status=$0", OutboxStatus.Pending), (await GetDigestAsync(ct)).Cursors, faults.LastSync.UtcDateTime.Ticks);
    }
    public async Task<PeerDiagnostic[]> GetPeerDiagnosticsAsync(CancellationToken ct = default)
    {
        using var db = database.Open(ct);
        return db.Rows<PeerNode>().Select(x => new PeerDiagnostic(x.NodeId, x.DeviceName, x.IPAddress, x.Role, x.Term, x.LastSeenTicks)).ToArray();
    }
}
