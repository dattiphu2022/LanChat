using LanChat.Application;
using LanChat.Domain;

namespace LanChat.Infrastructure;

public sealed partial class ChatStore(ChatDatabase database, NodeOptions options, NodeRuntimeState runtime, FaultInjection faults) : IChatStore, IDistributedStore, IDisposable
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private long _revision;
    private long _presenceRevision;
    public long PresenceRevision => Interlocked.Read(ref _presenceRevision);
    public long Revision => Interlocked.Read(ref _revision);
    internal async Task<T> WriteAsync<T>(Func<ChatDbContext, Task<T>> action, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var db = database.Open(ct);
            db.Begin();
            var result = await action(db).ConfigureAwait(false);
            db.Commit();
            if (db.ContentChanged) Interlocked.Increment(ref _revision);
            if (db.PresenceChanged) Interlocked.Increment(ref _presenceRevision);
            return result;
        }
        finally { _write.Release(); }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!System.Security.Cryptography.AesGcm.IsSupported) throw new PlatformNotSupportedException("Windows không hỗ trợ AES-GCM cho LanChat.");
        Directory.CreateDirectory(options.DataDirectory);
        if (File.Exists(Path.Combine(options.DataDirectory, "lanchat.db")))
        {
            using var existing = database.Open(ct);
            if (existing.Scalar("SELECT COUNT(*) FROM pragma_table_info('LocalSettings') WHERE name='ClusterId'") != 0 &&
                existing.Scalar("SELECT COUNT(*) FROM LocalSettings WHERE ClusterId<>$0 AND ClusterId<>$1", Guid.Empty, options.ClusterId) != 0)
                throw new InvalidOperationException("Database thuộc mạng chat khác. Hãy dùng thư mục dữ liệu riêng.");
        }
        database.Migrate(ct: ct);
        await WriteAsync(db =>
        {
            if (!db.Exists<LocalSettings>())
            {
                var user = new LocalUserProfile { Id = LanChat.Domain.AppIdentity.NewId(), DisplayName = options.InitialDisplayName, Department = options.InitialDepartment };
                db.Insert(user);
                db.Insert(new LocalSettings { Id = LanChat.Domain.AppIdentity.NewId(), NodeId = options.InitialNodeId ?? LanChat.Domain.AppIdentity.NewId(), UserId = user.Id, CreatedAtTicks = DateTime.UtcNow.Ticks, ClusterId = options.ClusterId });
            }
            return Task.FromResult(true);
        }, ct);
        await InitializeDeviceAsync(ct);
        await InitializeDistributedAsync(ct);
    }

    public async Task<NodeIdentity> GetIdentityAsync(CancellationToken ct = default)
    {
        using var db = database.Open(ct);
        var s = db.Required<LocalSettings>();
        return new NodeIdentity { NodeId = s.NodeId, UserId = s.UserId, DeviceName = Environment.MachineName, CreatedAt = new DateTimeOffset(s.CreatedAtTicks, TimeSpan.Zero) };
    }
    public async Task<LocalUserProfile> GetProfileAsync(CancellationToken ct = default)
    {
        using var db = database.Open(ct);
        var id = db.Required<LocalSettings>().UserId;
        return db.Required<LocalUserProfile>("Id=$0", id);
    }
    public async Task SaveProfileAsync(LocalUserProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 100)
            throw new ArgumentException("Tên hiển thị phải có từ 1 đến 100 ký tự.");
        await WriteAsync(async db =>
        {
            var local = db.Required<LocalSettings>();
            if (profile.Id != local.UserId) throw new InvalidOperationException("Không thể đổi định danh người dùng.");
            db.NoteContentChange();
            db.Execute("UPDATE Users SET DisplayName=$0,FullName=$1,Nickname=$2,Department=$3,Position=$4,Phone=$5,StatusMessage=$6,AvatarPath=$7 WHERE Id=$8",
                profile.DisplayName, profile.FullName, profile.Nickname, profile.Department, profile.Position, profile.Phone, profile.StatusMessage, profile.AvatarPath, profile.Id);
            await EmitAsync(db, local, ReplicationEventType.UserProfileUpdated, profile.Id, null, ToProfile(profile), ct);
            return true;
        }, ct);
    }
    public async Task<NodeInfoDto> GetNodeInfoAsync(CancellationToken ct = default)
    {
        var identity = await GetIdentityAsync(ct);
        var p = await GetProfileAsync(ct);
        using var db = database.Open(ct);
        var state = db.One<ClusterState>();
        var info = new NodeInfoDto(identity.NodeId, identity.DeviceName, options.ServicePort,
            new(p.Id, p.DisplayName, p.FullName, p.Nickname, p.Department, p.Position, p.Phone, p.StatusMessage),
            Role: runtime.Role, Term: state?.CurrentTerm ?? 0, CanLead: runtime.CanLead, DeviceKeys: Device.PublicKeys);
        return info with { KeyProof = Device.Sign(options.ClusterId, "discovery", info) };
    }

    public async Task<IReadOnlyList<UserSummary>> GetUsersAsync(string search = "", CancellationToken ct = default)
    {
        using var db = database.Open(ct);
        var local = db.Required<LocalSettings>().UserId;
        // Only the small contact directory is searched here; messages use the indexed conversation query.
        var users = db.Rows<LocalUserProfile>("Id<>$0", local).Where(u => NameMatches(u.DisplayName, search));
        var peers = db.Rows<PeerNode>("1 ORDER BY LastSeenTicks DESC").ToLookup(x => x.UserId);
        var cutoff = DateTime.UtcNow.Ticks - PeerPresenceWindow.Ticks;
        return users.Select(u =>
        {
            var p = peers[u.Id].FirstOrDefault();
            return new UserSummary(u.Id, u.DisplayName, u.Department, p?.DeviceName ?? "", p?.IPAddress ?? "", p?.ServicePort ?? 0,
                p?.NodeId ?? Guid.Empty, p?.LastSeenTicks ?? 0, p is null ? PresenceStatus.Unknown : p.LastSeenTicks >= cutoff ? PresenceStatus.Online : PresenceStatus.Offline);
        }).OrderByDescending(x => x.Presence == PresenceStatus.Online).ThenBy(x => x.DisplayName).ToArray();
    }

    private async Task<Guid> EnsureDirectAsync(ChatDbContext db, Guid local, Guid other, CancellationToken ct)
    {
        if (local == other || !db.Exists<LocalUserProfile>("Id=$0", other)) throw new ArgumentException("Người nhận chưa có trong danh bạ.");
        var id = DirectConversation.GetId(options.ClusterId, local, other);
        if (!db.Exists<Conversation>("Id=$0", id))
        {
            db.Insert(new Conversation { Id = id, Kind = ConversationKind.Direct });
            db.Insert(new ConversationMember { Id = LanChat.Domain.AppIdentity.NewId(), ConversationId = id, UserId = local });
            db.Insert(new ConversationMember { Id = LanChat.Domain.AppIdentity.NewId(), ConversationId = id, UserId = other });
        }
        return id;
    }
    public Task<Guid> OpenDirectAsync(Guid userId, CancellationToken ct = default) => WriteAsync(async db =>
    {
        var id = await EnsureDirectAsync(db, db.Required<LocalSettings>().UserId, userId, ct);
        db.Required<Conversation>("Id=$0", id).IsHiddenLocal = false;
        return id;
    }, ct);

    public Task ShowConversationAsync(Guid id, CancellationToken ct = default) => WriteAsync(db =>
    {
        LocalConversation(db, id).IsHiddenLocal = false;
        return Task.FromResult(true);
    }, ct);

    public Task DeleteConversationLocalAsync(Guid id, CancellationToken ct = default) => WriteAsync(db =>
    {
        var conversation = LocalConversation(db, id);
        conversation.IsHiddenLocal = true;
        conversation.IsPinned = false;
        db.Execute("UPDATE Messages SET IsDeletedLocal=1 WHERE ConversationId=$0 AND IsDeletedLocal=0", id);
        return Task.FromResult(true);
    }, ct);

    private static Conversation LocalConversation(ChatDbContext db, Guid id)
    {
        var local = db.Required<LocalSettings>().UserId;
        if (!db.Exists<ConversationMember>("ConversationId=$0 AND UserId=$1", id, local))
            throw new InvalidOperationException("Bạn không còn là thành viên cuộc trò chuyện.");
        return db.Required<Conversation>("Id=$0", id);
    }

    public Task<IncomingMessageBatch> GetIncomingMessagesAsync(long? after, CancellationToken ct = default)
    {
        using var db = database.Open(ct);
        // Local insertion order also catches delayed messages with an older sender timestamp.
        var position = db.Scalar("SELECT COALESCE(MAX(rowid),0) FROM Messages");
        if (!after.HasValue || after.Value >= position)
            return Task.FromResult(new IncomingMessageBatch(position, []));

        var local = db.Required<LocalSettings>().UserId;
        using var command = db.Command(
            "SELECT m.ConversationId,m.Text,m.Kind FROM Messages m JOIN Conversations c ON c.Id=m.ConversationId " +
            "WHERE m.rowid>$0 AND m.rowid<=$1 AND m.SenderUserId<>$2 AND m.IsRead=0 AND m.IsDeletedLocal=0 AND m.IsRecalled=0 " +
            "AND m.Kind<>$3 AND c.IsMuted=0 AND c.IsHiddenLocal=0 " +
            "AND EXISTS(SELECT 1 FROM ConversationMembers cm WHERE cm.ConversationId=c.Id AND cm.UserId=$2) " +
            "ORDER BY m.rowid", after.Value, position, local, MessageKind.System);
        using var reader = command.ExecuteReader();
        var messages = new List<IncomingMessageNotice>();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var preview = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(preview))
                preview = (MessageKind)reader.GetInt32(2) == MessageKind.Image ? "Đã gửi ảnh" : "Đã gửi tệp";
            messages.Add(new IncomingMessageNotice(Guid.Parse(reader.GetString(0)), preview));
        }
        return Task.FromResult(new IncomingMessageBatch(position, messages.ToArray()));
    }

    public async Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(string search = "", CancellationToken ct = default)
    {
        var users = (await GetUsersAsync(search, ct)).ToDictionary(x => x.UserId);
        using var db = database.Open(ct);
        var local = (db.Required<LocalSettings>()).UserId;
        var conversations = db.Rows<Conversation>("IsHiddenLocal=0 AND Id IN (SELECT ConversationId FROM ConversationMembers WHERE UserId=$0)", local);
        var members = db.Rows<ConversationMember>().ToLookup(m => m.ConversationId);
        var result = new List<ConversationSummary>();
        foreach (var c in conversations)
        {
            if (c.Kind == ConversationKind.Group)
            {
                var group = db.One<Group>("Id=$0 AND IsDeleted=0", c.Id);
                if (group is null || (search.Length > 0 && !NameMatches(group.Name, search))) continue;
                var groupLast = db.One<Message>("ConversationId=$0 AND IsDeletedLocal=0" + NewestFirst + " LIMIT 1", c.Id);
                var groupUnread = (int)db.Count<Message>("ConversationId=$0 AND SenderUserId<>$1 AND IsRead=0 AND IsDeletedLocal=0", c.Id, local);
                result.Add(new(c.Id, Guid.Empty, group.Name, groupLast?.Text ?? "Nhóm mới", groupLast?.CreatedAtTicks ?? 0, groupUnread,
                    c.IsPinned, c.IsMuted, PresenceStatus.Unknown, ConversationKind.Group, group.AvatarBase64)); continue;
            }
            var otherId = members[c.Id].FirstOrDefault(x => users.ContainsKey(x.UserId))?.UserId;
            if (otherId is null) continue;
            var u = users[otherId.Value];
            var last = db.One<Message>("ConversationId=$0 AND IsDeletedLocal=0" + NewestFirst + " LIMIT 1", c.Id);
            var unread = (int)db.Count<Message>("ConversationId=$0 AND SenderUserId=$1 AND IsRead=0 AND IsDeletedLocal=0", c.Id, u.UserId);
            result.Add(new(c.Id, u.UserId, u.DisplayName, last?.Text ?? "Bắt đầu trò chuyện", last?.CreatedAtTicks ?? 0, unread, c.IsPinned, c.IsMuted, u.Presence)
                { Department = u.Department });
        }
        return result.OrderByDescending(x => x.IsPinned).ThenByDescending(x => x.LastMessageTicks).ToArray();
    }
    public async Task<IReadOnlyList<Message>> GetMessagesAsync(Guid conversationId, long? beforeTicks = null, CancellationToken ct = default, int limit = 200)
    {
        using var db = database.Open(ct);
        var page = db.Rows<Message>("ConversationId=$0 AND IsDeletedLocal=0 AND ($1 IS NULL OR PhysicalTicks<$1)" + NewestFirst + " LIMIT $2", conversationId, beforeTicks, Math.Max(1, limit));
        Array.Reverse(page); return page;
    }
    public Task<Message> SaveOutgoingAsync(Guid recipientId, string text, CancellationToken ct = default) => WriteAsync(async db =>
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) throw new ArgumentException("Nội dung tin nhắn không hợp lệ.");
        var s = db.Required<LocalSettings>();
        var conversation = await EnsureDirectAsync(db, s.UserId, recipientId, ct);
        db.Required<Conversation>("Id=$0", conversation).IsHiddenLocal = false;
        (s.PhysicalTicks, s.LogicalCounter) = HybridClock.Advance(s.PhysicalTicks, s.LogicalCounter);
        var message = new Message { Id = LanChat.Domain.AppIdentity.NewId(), ClientMessageId = LanChat.Domain.AppIdentity.NewId(), ConversationId = conversation,
            SenderUserId = s.UserId, RecipientUserId = recipientId, OriginNodeId = s.NodeId, OriginSequence = ++s.LastSequence,
            Kind = MessageKind.Text, Text = text, CreatedAtTicks = DateTime.UtcNow.Ticks, PhysicalTicks = s.PhysicalTicks,
            LogicalCounter = s.LogicalCounter, Status = MessageStatus.LocalPending, IsRead = true };
        db.Insert(message);
        db.SaveChanges();
        await EmitMessageAsync(db, s, message, null, ct);
        return message;
    }, ct);

    public async Task SetConversationAsync(Guid id, bool? pin = null, bool? mute = null, bool markRead = false, CancellationToken ct = default)
    {
        await WriteAsync(async db =>
        {
            var c = db.Required<Conversation>("Id=$0", id);
            if (pin.HasValue) c.IsPinned = pin.Value;
            if (mute.HasValue) c.IsMuted = mute.Value;
            if (markRead && db.Exists<Message>("ConversationId=$0 AND IsRead=0 AND IsDeletedLocal=0", id))
            {
                c.LastReadTicks = DateTime.UtcNow.Ticks;
                var s = db.Required<LocalSettings>();
                var unread = db.Rows<Message>("ConversationId=$0 AND IsRead=0 AND IsDeletedLocal=0", id);
                foreach (var m in unread)
                {
                    m.IsRead = true;
                    await EmitAsync(db, s, ReplicationEventType.ReceiptUpdated, m.Id, id, new ReceiptChange(true, DateTime.UtcNow.Ticks), ct);
                }
            }
            return true;
        }, ct);
    }
    public async Task<int> GetPendingCountAsync(CancellationToken ct = default)
    {
        using var db = database.Open(ct);
        return (int)db.Count<Message>("Status=$0", MessageStatus.LocalPending);
    }
    private const string NewestFirst = " ORDER BY PhysicalTicks DESC,LogicalCounter DESC,OriginNodeId DESC,Id DESC";
    public static bool NameMatches(string name, string search) => System.Globalization.CultureInfo.GetCultureInfo("vi-VN").CompareInfo.IndexOf(
        name.Replace('đ', 'd').Replace('Đ', 'D'), search.Replace('đ', 'd').Replace('Đ', 'D'),
        System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;

    public Task<Message[]> GetOlderMessagesAsync(Guid conversationId, Message before, int limit, CancellationToken ct = default)
    {
        using var db = database.Open(ct);
        var page = db.Rows<Message>("ConversationId=$0 AND IsDeletedLocal=0 AND (PhysicalTicks,LogicalCounter,OriginNodeId,Id)<($1,$2,$3,$4)" + NewestFirst + " LIMIT $5",
            conversationId, before.PhysicalTicks, before.LogicalCounter, before.OriginNodeId, before.Id, Math.Max(1, limit));
        Array.Reverse(page);
        return Task.FromResult(page);
    }
    public void Dispose() { _device?.Dispose(); _write.Dispose(); }
}
