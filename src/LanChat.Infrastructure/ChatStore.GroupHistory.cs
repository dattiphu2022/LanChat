using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;

namespace LanChat.Infrastructure;

public sealed partial class ChatStore
{
    private static string? SharedHistoryKey(ChatDbContext db, ReplicationEvent item)
    {
        using var query = db.Command("SELECT WrappedKey FROM GroupHistoryKeys WHERE EventId=$0 AND GroupId=$1", item.Id, item.ConversationId);
        return query.ExecuteScalar() as string;
    }

    private static string GroupHistoryRevision(ChatDbContext db)
    {
        var local = db.Required<LocalSettings>().UserId;
        var groups = db.Rows<Group>("IsDeleted=0 AND Id IN (SELECT GroupId FROM GroupMembers WHERE UserId=$0)", local)
            .OrderBy(group => group.Id).Select(group => new { group.Id, group.LastCommandId }).ToArray();
        return FrameworkIO.Hex(FrameworkIO.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Groups = groups,
            Keys = db.Scalar("SELECT COUNT(*) FROM GroupHistoryKeys")
        })));
    }

    internal Task<EventBatch> AddGroupHistoryAsync(EventBatch batch, Guid recipientNodeId, CancellationToken ct)
    {
        using var db = database.Open(ct);
        var peer = db.One<PeerNode>("NodeId=$0", recipientNodeId);
        var local = db.Required<LocalSettings>();
        if (peer is null || FindDeviceKeys(db, peer.UserId) is not { } recipientKeys) return Task.FromResult(batch);
        var groups = db.Rows<Group>("IsDeleted=0 AND Id IN (SELECT GroupId FROM GroupMembers WHERE UserId=$0) " +
            "AND Id IN (SELECT GroupId FROM GroupMembers WHERE UserId=$1)", local.UserId, peer.UserId).Select(group => group.Id).ToHashSet();
        var grants = new List<GroupHistoryKey>();
        foreach (var item in batch.Events)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Type == ReplicationEventType.UserProfileUpdated || item.ConversationId is not Guid groupId || !groups.Contains(groupId)) continue;
            var envelope = DeviceIdentity.ParseEnvelope(item.PayloadJson);
            if (envelope.Recipients.Any(slot => slot.UserId == peer.UserId)) continue;
            var created = db.One<ReplicationEvent>("EntityId=$0 AND Type=$1 ORDER BY PhysicalTicks,LogicalCounter,OriginNodeId,Id LIMIT 1",
                item.EntityId, ReplicationEventType.MessageCreated);
            if (created is null) continue;
            var content = ReadEncryptedEvent<MessageContent>(db, created, local.UserId);
            if (content is null || content.Message.RecipientUserId != Guid.Empty || content.Message.ConversationId != groupId) continue;
            var stored = db.Required<ReplicationEvent>("Id=$0", item.Id);
            var shared = SharedHistoryKey(db, stored);
            if (!envelope.Recipients.Any(slot => slot.UserId == local.UserId) && shared is null) continue;
            var key = Device.RewrapContentKey(envelope, local.UserId, shared, recipientKeys);
            var grant = new GroupHistoryKey(item.Id, groupId, local.NodeId, local.UserId, peer.UserId, key, Device.PublicKeys);
            grants.Add(grant with { Signature = Device.Sign(options.ClusterId, "group-history", grant) });
        }
        return Task.FromResult(batch with { GroupHistoryKeys = grants.ToArray() });
    }

    private void ImportGroupHistoryKeys(ChatDbContext db, GroupHistoryKey[] grants, HashSet<Guid> changed)
    {
        var local = db.Required<LocalSettings>();
        foreach (var grant in grants)
        {
            if (grant is null || grant.RecipientUserId != local.UserId || grant.GrantorNodeId == Guid.Empty || grant.GrantorUserId == Guid.Empty)
                throw new InvalidDataException("Định danh chia sẻ lịch sử nhóm không hợp lệ.");
            DeviceIdentity.Verify(options.ClusterId, "group-history", grant with { Signature = null }, grant.DeviceKeys, grant.Signature);
            PinDevice(db, grant.GrantorNodeId, grant.GrantorUserId, grant.DeviceKeys);
            if (!db.Exists<Group>("Id=$0 AND IsDeleted=0", grant.GroupId) ||
                !db.Exists<GroupMember>("GroupId=$0 AND UserId=$1", grant.GroupId, local.UserId) ||
                !db.Exists<GroupMember>("GroupId=$0 AND UserId=$1", grant.GroupId, grant.GrantorUserId)) continue;
            var item = db.One<ReplicationEvent>("Id=$0", grant.EventId);
            if (item is null || item.Type == ReplicationEventType.UserProfileUpdated || item.ConversationId != grant.GroupId)
                throw new InvalidDataException("Khóa lịch sử không khớp sự kiện nhóm.");
            var envelope = DeviceIdentity.ParseEnvelope(item.PayloadJson);
            if (envelope.Recipients.Any(slot => slot.UserId == local.UserId) || SharedHistoryKey(db, item) is not null) continue;
            var opened = Device.Open(envelope, local.UserId, EventEncryptionContext(item.Id), grant.WrappedKey)!;
            try
            {
                if (item.Type == ReplicationEventType.MessageCreated)
                {
                    var content = JsonSerializer.Deserialize<MessageContent>(opened) ?? throw new InvalidDataException("Thiếu nội dung lịch sử nhóm.");
                    if (content.Message.RecipientUserId != Guid.Empty || content.Message.ConversationId != grant.GroupId)
                        throw new InvalidDataException("Chỉ chia sẻ lịch sử của tin nhắn nhóm.");
                }
            }
            finally { Array.Clear(opened, 0, opened.Length); }
            db.Execute("INSERT INTO GroupHistoryKeys VALUES ($0,$1,$2)", item.Id, grant.GroupId, grant.WrappedKey);
            db.NoteContentChange();
            changed.Add(item.EntityId);
        }
    }

    internal async Task RepairGroupHistoryAsync(CancellationToken ct)
    {
        long after = 0;
        while (true)
        {
            var repaired = await WriteAsync(async db =>
            {
                using var query = db.Command("SELECT e.rowid,e.EntityId FROM ReplicationEvents e JOIN Groups g ON g.Id=e.ConversationId " +
                    "WHERE e.rowid>$0 AND e.Type=$1 AND g.IsDeleted=0 AND EXISTS(SELECT 1 FROM GroupMembers m " +
                    "WHERE m.GroupId=g.Id AND m.UserId=$2) ORDER BY e.rowid LIMIT 100", after, ReplicationEventType.MessageCreated, db.Required<LocalSettings>().UserId);
                var rows = new List<(long Position, Guid Id)>();
                using (var reader = query.ExecuteReader()) while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetGuid(1)));
                foreach (var row in rows) await ProjectEntityAsync(db, row.Id, ct);
                return rows;
            }, ct).ConfigureAwait(false);
            if (repaired.Count == 0) return;
            after = repaired.Last().Position;
        }
    }
}
