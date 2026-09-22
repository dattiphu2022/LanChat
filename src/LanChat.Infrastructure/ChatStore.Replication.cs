using System.Security.Cryptography;
using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;
using Microsoft.EntityFrameworkCore;

namespace LanChat.Infrastructure;

public sealed partial class ChatStore
{
    internal static UserProfileDto ToProfile(LocalUserProfile p) => new(p.Id, p.DisplayName, p.FullName, p.Nickname, p.Department, p.Position, p.Phone, p.StatusMessage);
    internal static EventDto ToEvent(ReplicationEvent e) => new(e.Id, e.OriginNodeId, e.OriginSequence, e.ActorUserId, e.EntityId, e.ConversationId,
        e.Type, e.PayloadJson, e.PhysicalTicks, e.LogicalCounter, e.CreatedAtTicks, e.Signature);
    private string SignEvent(EventDto e) => Convert.ToBase64String(HMACSHA256.HashData(options.ClusterSecret, JsonSerializer.SerializeToUtf8Bytes(e with { Signature = "" })));
    private async Task InitializeDistributedAsync(CancellationToken ct)
    {
        await WriteAsync(async db =>
        {
            var s = await db.Settings.SingleAsync(ct);
            if (s.ClusterId != Guid.Empty && s.ClusterId != options.ClusterId) throw new InvalidOperationException("Database thuộc mạng chat khác. Hãy dùng thư mục dữ liệu riêng.");
            s.ClusterId = options.ClusterId;
            if (!await db.Cluster.AnyAsync(ct)) db.Cluster.Add(new ClusterState { Id = Guid.CreateVersion7(), ClusterId = options.ClusterId });
            if (!await db.Events.AnyAsync(x => x.OriginNodeId == s.NodeId && x.Type == ReplicationEventType.UserProfileUpdated, ct))
                await EmitAsync(db, s, ReplicationEventType.UserProfileUpdated, s.UserId, null, ToProfile(await db.Users.SingleAsync(x => x.Id == s.UserId, ct)), ct);
            var oldMessages = await db.Messages.Where(m => m.Kind != MessageKind.System && !db.Events.Any(e => e.EntityId == m.Id && e.Type == ReplicationEventType.MessageCreated)).ToListAsync(ct);
            foreach (var message in oldMessages) await EmitMessageAsync(db, s, message, null, ct);
            return true;
        }, ct);
    }
    private async Task<EventDto> EmitAsync<T>(ChatDbContext db, LocalSettings s, ReplicationEventType type, Guid entityId,
        Guid? conversationId, T payload, CancellationToken ct, Guid? actor = null)
    {
        (s.PhysicalTicks, s.LogicalCounter) = HybridClock.Advance(s.PhysicalTicks, s.LogicalCounter);
        var e = new ReplicationEvent { Id = Guid.CreateVersion7(), OriginNodeId = s.NodeId, OriginSequence = ++s.EventSequence,
            ActorUserId = actor ?? s.UserId, EntityId = entityId, ConversationId = conversationId, Type = type,
            PayloadJson = JsonSerializer.Serialize(payload), PhysicalTicks = s.PhysicalTicks, LogicalCounter = s.LogicalCounter, CreatedAtTicks = DateTime.UtcNow.Ticks };
        e.Signature = SignEvent(ToEvent(e));
        db.Events.Add(e); db.Outbox.Add(new OutboxItem { Id = Guid.CreateVersion7(), ReplicationEventId = e.Id });
        var cursor = await db.Cursors.SingleOrDefaultAsync(x => x.OriginNodeId == s.NodeId, ct);
        if (cursor is null) { cursor = new ReplicationCursor { Id = Guid.CreateVersion7(), OriginNodeId = s.NodeId }; db.Cursors.Add(cursor); }
        cursor.LastOriginSequence = s.EventSequence;
        await db.SaveChangesAsync(ct);
        await ProjectEntityAsync(db, e.EntityId, ct);
        return ToEvent(e);
    }
    private async Task EmitMessageAsync(ChatDbContext db, LocalSettings s, Message message, AttachmentDto[]? attachments, CancellationToken ct)
    {
        var sender = await db.Users.SingleAsync(x => x.Id == message.SenderUserId, ct);
        var recipient = await db.Users.SingleOrDefaultAsync(x => x.Id == message.RecipientUserId, ct);
        var audience = await db.Members.Where(x => x.ConversationId == message.ConversationId).Select(x => x.UserId).ToArrayAsync(ct);
        var content = new MessageContent(PendingMessageDeliveryService.ToDto(message), ToProfile(sender), recipient is null ? null : ToProfile(recipient),
            message.ReplyToMessageId, message.ForwardedFromMessageId, attachments, audience);
        await EmitAsync(db, s, ReplicationEventType.MessageCreated, message.Id, message.ConversationId, content, ct, message.SenderUserId);
    }
    public async Task<ReplicationDigest> GetDigestAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return new(await db.Cursors.AsNoTracking().Select(x => new CursorDto(x.OriginNodeId, x.LastOriginSequence)).ToArrayAsync(ct));
    }
    public async Task<EventBatch> GetEventRangeAsync(EventRangeRequest request, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Events.AsNoTracking().Where(x => x.OriginNodeId == request.OriginNodeId && x.OriginSequence > request.AfterSequence)
            .OrderBy(x => x.OriginSequence).Take(Math.Clamp(request.Limit, 1, 100)).ToArrayAsync(ct);
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
        if (batch.Events.Length > 100) throw new InvalidDataException("Lô sự kiện quá lớn.");
        var s = await db.Settings.SingleAsync(ct);
        var changed = new HashSet<Guid>(); var origins = new HashSet<Guid>();
        foreach (var e in batch.Events)
        {
            ValidateEvent(e);
            var old = await db.Events.SingleOrDefaultAsync(x => x.Id == e.Id || (x.OriginNodeId == e.OriginNodeId && x.OriginSequence == e.OriginSequence), ct);
            if (old is not null)
            {
                if (ToEvent(old) != e) throw new InvalidDataException("Xung đột sự kiện bất biến.");
                continue;
            }
            if (e.OriginNodeId == s.NodeId) throw new InvalidDataException("Không nhận sự kiện mới giả danh node cục bộ.");
            db.Events.Add(new ReplicationEvent { Id = e.Id, OriginNodeId = e.OriginNodeId, OriginSequence = e.OriginSequence,
                ActorUserId = e.ActorUserId, EntityId = e.EntityId, ConversationId = e.ConversationId, Type = e.Type, PayloadJson = e.PayloadJson,
                PhysicalTicks = e.PhysicalTicks, LogicalCounter = e.LogicalCounter, CreatedAtTicks = e.CreatedAtTicks, Signature = e.Signature });
            (s.PhysicalTicks, s.LogicalCounter) = HybridClock.Advance(s.PhysicalTicks, s.LogicalCounter, e.PhysicalTicks, e.LogicalCounter);
            changed.Add(e.EntityId); origins.Add(e.OriginNodeId);
            await db.SaveChangesAsync(ct);
        }
        foreach (var origin in origins)
        {
            var cursor = await db.Cursors.SingleOrDefaultAsync(x => x.OriginNodeId == origin, ct);
            if (cursor is null) { cursor = new ReplicationCursor { Id = Guid.CreateVersion7(), OriginNodeId = origin }; db.Cursors.Add(cursor); }
            // Never skip a gap, even when a later event arrived through realtime delivery.
            while (true)
            {
                var sequences = await db.Events.Where(x => x.OriginNodeId == origin && x.OriginSequence > cursor.LastOriginSequence)
                    .OrderBy(x => x.OriginSequence).Select(x => x.OriginSequence).Take(500).ToArrayAsync(ct);
                var start = cursor.LastOriginSequence;
                foreach (var sequence in sequences) { if (sequence != cursor.LastOriginSequence + 1) break; cursor.LastOriginSequence = sequence; }
                if (sequences.Length < 500 || cursor.LastOriginSequence == start || cursor.LastOriginSequence != sequences[^1]) break;
            }
        }
        foreach (var id in changed) await ProjectEntityAsync(db, id, ct);
        await db.SaveChangesAsync(ct);
        foreach (var id in changed)
        {
            var m = await db.Messages.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (m is not null && m.SenderUserId != s.UserId && await db.Members.AnyAsync(x => x.ConversationId == m.ConversationId && x.UserId == s.UserId, ct)
                && !await db.Receipts.AnyAsync(x => x.MessageId == id && x.UserId == s.UserId, ct))
                await EmitAsync(db, s, ReplicationEventType.ReceiptUpdated, id, m.ConversationId, new ReceiptChange(false, DateTime.UtcNow.Ticks), ct);
        }
        return new EventAck(batch.Events.Select(x => x.Id).ToArray());
    }, ct);
    private void ValidateEvent(EventDto e)
    {
        if (e.Id == Guid.Empty || e.EntityId == Guid.Empty || e.ActorUserId == Guid.Empty || e.OriginNodeId == Guid.Empty || e.OriginSequence < 1 ||
            e.PayloadJson.Length > 200000 || e.PhysicalTicks < 1 || e.PhysicalTicks > DateTime.UtcNow.AddDays(1).Ticks || e.LogicalCounter < 0 || e.LogicalCounter > 10000000 ||
            !Enum.IsDefined(e.Type) || !CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(SignEvent(e)), Convert.FromBase64String(e.Signature)))
            throw new InvalidDataException("Sự kiện không hợp lệ.");
    }
    public async Task<PeerEndpoint[]> GetPeerEndpointsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Peers.AsNoTracking().Select(x => new PeerEndpoint(x.NodeId, x.IPAddress, x.ServicePort)).ToArrayAsync(ct);
    }
    public async Task<EventDto[]> GetOutboxAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var now = DateTime.UtcNow.Ticks;
        var ids = await db.Outbox.Where(x => x.Status == OutboxStatus.Pending && x.NextRetryTicks <= now).OrderBy(x => x.NextRetryTicks).Select(x => x.ReplicationEventId).Take(20).ToArrayAsync(ct);
        var events = await db.Events.AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.OriginSequence).ToArrayAsync(ct);
        var result = new List<EventDto>(); var bytes = 0;
        foreach (var row in events)
        {
            var item = ToEvent(row); var length = JsonSerializer.SerializeToUtf8Bytes(item).Length;
            if (result.Count > 0 && bytes + length > 500_000) break;
            result.Add(item); bytes += length;
        }
        return result.ToArray();
    }
    public async Task RecordOutboxResultAsync(Guid[] eventIds, bool success, CancellationToken ct = default)
    {
        await WriteAsync(async db =>
        {
            var rows = await db.Outbox.Where(x => eventIds.Contains(x.ReplicationEventId)).ToArrayAsync(ct);
            foreach (var row in rows)
            {
                if (success) { row.Status = OutboxStatus.Replicated; row.LastError = null; }
                else { row.RetryCount++; row.NextRetryTicks = DateTime.UtcNow.AddSeconds(Math.Min(30, Math.Pow(2, Math.Min(5, row.RetryCount)))).Ticks; row.LastError = "Chưa kết nối được peer."; }
            }
            if (success)
            {
                var messageIds = await db.Events.Where(x => eventIds.Contains(x.Id) && x.Type == ReplicationEventType.MessageCreated).Select(x => x.EntityId).ToArrayAsync(ct);
                await db.Messages.Where(x => messageIds.Contains(x.Id) && x.Status == MessageStatus.LocalPending).ExecuteUpdateAsync(x => x.SetProperty(m => m.Status, MessageStatus.Synced), ct);
            }
            return true;
        }, ct);
    }
    public async Task<DistributedDiagnostics> GetDistributedDiagnosticsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return new(await db.Events.CountAsync(ct), await db.Outbox.CountAsync(x => x.Status == OutboxStatus.Pending, ct), (await GetDigestAsync(ct)).Cursors, faults.LastSync.UtcTicks);
    }
    public async Task<PeerDiagnostic[]> GetPeerDiagnosticsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Peers.AsNoTracking().Select(x => new PeerDiagnostic(x.NodeId, x.DeviceName, x.IPAddress, x.Role, x.Term, x.LastSeenTicks)).ToArrayAsync(ct);
    }
}
