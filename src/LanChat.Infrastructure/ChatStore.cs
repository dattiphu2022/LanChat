using LanChat.Application;
using LanChat.Domain;
using Microsoft.EntityFrameworkCore;

namespace LanChat.Infrastructure;

public sealed class ChatStore(IDbContextFactory<ChatDbContext> factory, NodeOptions options) : IChatStore, IDisposable
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private async Task<T> WriteAsync<T>(Func<ChatDbContext, Task<T>> action, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await action(db);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        finally { _write.Release(); }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(options.DataDirectory);
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.MigrateAsync(ct);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
        }
        await WriteAsync(async db =>
        {
            if (!await db.Settings.AnyAsync(ct))
            {
                var user = new LocalUserProfile { Id = Guid.CreateVersion7(), DisplayName = options.InitialDisplayName };
                db.Users.Add(user);
                db.Settings.Add(new LocalSettings { Id = Guid.CreateVersion7(), NodeId = Guid.CreateVersion7(), UserId = user.Id, CreatedAtTicks = DateTime.UtcNow.Ticks });
            }
            return true;
        }, ct);
    }

    public async Task<NodeIdentity> GetIdentityAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var s = await db.Settings.AsNoTracking().SingleAsync(ct);
        return new NodeIdentity { NodeId = s.NodeId, UserId = s.UserId, DeviceName = Environment.MachineName, CreatedAt = new DateTimeOffset(s.CreatedAtTicks, TimeSpan.Zero) };
    }
    public async Task<LocalUserProfile> GetProfileAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var id = await db.Settings.Select(x => x.UserId).SingleAsync(ct);
        return await db.Users.AsNoTracking().SingleAsync(x => x.Id == id, ct);
    }
    public async Task SaveProfileAsync(LocalUserProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 100)
            throw new ArgumentException("Tên hiển thị phải có từ 1 đến 100 ký tự.");
        await WriteAsync(async db =>
        {
            var local = await db.Settings.SingleAsync(ct);
            if (profile.Id != local.UserId) throw new InvalidOperationException("Không thể đổi định danh người dùng.");
            db.Users.Update(profile);
            return true;
        }, ct);
    }
    public async Task<NodeInfoDto> GetNodeInfoAsync(CancellationToken ct = default)
    {
        var identity = await GetIdentityAsync(ct);
        var p = await GetProfileAsync(ct);
        return new(identity.NodeId, identity.DeviceName, options.ServicePort,
            new(p.Id, p.DisplayName, p.FullName, p.Nickname, p.Department, p.Position, p.Phone, p.StatusMessage));
    }

    private static async Task UpsertPeerAsync(ChatDbContext db, NodeInfoDto peer, string address, CancellationToken ct)
    {
        if (peer.ProtocolVersion != 1 || peer.NodeId == Guid.Empty || peer.User.Id == Guid.Empty ||
            peer.ServicePort is < 1024 or > 65535 || string.IsNullOrWhiteSpace(peer.User.DisplayName) || peer.User.DisplayName.Length > 100)
            throw new InvalidDataException("Thông tin peer không hợp lệ.");
        var known = await db.Peers.SingleOrDefaultAsync(x => x.NodeId == peer.NodeId, ct);
        if (known is null)
        {
            known = new PeerNode { Id = Guid.CreateVersion7(), NodeId = peer.NodeId };
            db.Peers.Add(known);
        }
        known.UserId = peer.User.Id; known.DeviceName = peer.DeviceName;
        known.IPAddress = address; known.ServicePort = peer.ServicePort; known.LastSeenTicks = DateTime.UtcNow.Ticks;
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == peer.User.Id, ct);
        if (user is null) { user = new LocalUserProfile { Id = peer.User.Id }; db.Users.Add(user); }
        user.DisplayName = peer.User.DisplayName; user.FullName = peer.User.FullName; user.Nickname = peer.User.Nickname;
        user.Department = peer.User.Department; user.Position = peer.User.Position; user.Phone = peer.User.Phone; user.StatusMessage = peer.User.StatusMessage;
    }
    public async Task RememberPeerAsync(NodeInfoDto peer, string address, CancellationToken ct = default)
    {
        await WriteAsync(async db =>
        {
            var local = await db.Settings.SingleAsync(ct);
            if (peer.NodeId != local.NodeId && peer.User.Id != local.UserId) await UpsertPeerAsync(db, peer, address, ct);
            return true;
        }, ct);
    }
    public async Task<IReadOnlyList<UserSummary>> GetUsersAsync(string search = "", CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var local = await db.Settings.Select(x => x.UserId).SingleAsync(ct);
        // Only the small contact directory is searched here; messages use the indexed conversation query.
        var users = await db.Users.AsNoTracking().Where(x => x.Id != local && (search == "" || EF.Functions.Like(x.DisplayName, "%" + search + "%"))).ToListAsync(ct);
        var peers = await db.Peers.AsNoTracking().ToListAsync(ct);
        var cutoff = DateTime.UtcNow.AddSeconds(-25).Ticks;
        return users.Select(u =>
        {
            var p = peers.Where(x => x.UserId == u.Id).MaxBy(x => x.LastSeenTicks);
            return new UserSummary(u.Id, u.DisplayName, u.Department, p?.DeviceName ?? "", p?.IPAddress ?? "", p?.ServicePort ?? 0,
                p?.NodeId ?? Guid.Empty, p?.LastSeenTicks ?? 0, p is null ? PresenceStatus.Unknown : p.LastSeenTicks >= cutoff ? PresenceStatus.Online : PresenceStatus.Offline);
        }).OrderByDescending(x => x.Presence == PresenceStatus.Online).ThenBy(x => x.DisplayName).ToArray();
    }

    private async Task<Guid> EnsureDirectAsync(ChatDbContext db, Guid local, Guid other, CancellationToken ct)
    {
        if (local == other || !await db.Users.AnyAsync(x => x.Id == other, ct)) throw new ArgumentException("Người nhận chưa có trong danh bạ.");
        var id = DirectConversation.GetId(options.ClusterId, local, other);
        if (!await db.Conversations.AnyAsync(x => x.Id == id, ct))
        {
            db.Conversations.Add(new Conversation { Id = id, Kind = ConversationKind.Direct });
            db.Members.AddRange(new ConversationMember { Id = Guid.CreateVersion7(), ConversationId = id, UserId = local },
                new ConversationMember { Id = Guid.CreateVersion7(), ConversationId = id, UserId = other });
        }
        return id;
    }
    public Task<Guid> OpenDirectAsync(Guid userId, CancellationToken ct = default) => WriteAsync(async db =>
        await EnsureDirectAsync(db, (await db.Settings.SingleAsync(ct)).UserId, userId, ct), ct);

    public async Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(string search = "", CancellationToken ct = default)
    {
        var users = (await GetUsersAsync(search, ct)).ToDictionary(x => x.UserId);
        await using var db = await factory.CreateDbContextAsync(ct);
        var conversations = await db.Conversations.AsNoTracking().ToListAsync(ct);
        var members = await db.Members.AsNoTracking().ToListAsync(ct);
        var result = new List<ConversationSummary>();
        foreach (var c in conversations)
        {
            var otherId = members.FirstOrDefault(x => x.ConversationId == c.Id && users.ContainsKey(x.UserId))?.UserId;
            if (otherId is null) continue;
            var u = users[otherId.Value];
            var last = await db.Messages.AsNoTracking().Where(x => x.ConversationId == c.Id)
                .OrderByDescending(x => x.PhysicalTicks).ThenByDescending(x => x.LogicalCounter).ThenByDescending(x => x.OriginNodeId).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
            var unread = await db.Messages.CountAsync(x => x.ConversationId == c.Id && x.SenderUserId == u.UserId && !x.IsRead, ct);
            result.Add(new(c.Id, u.UserId, u.DisplayName, last?.Text ?? "Bắt đầu trò chuyện", last?.CreatedAtTicks ?? 0, unread, c.IsPinned, c.IsMuted, u.Presence));
        }
        return result.OrderByDescending(x => x.IsPinned).ThenByDescending(x => x.LastMessageTicks).ToArray();
    }
    public async Task<IReadOnlyList<Message>> GetMessagesAsync(Guid conversationId, long? beforeTicks = null, CancellationToken ct = default, int limit = 200)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.Messages.AsNoTracking().Where(x => x.ConversationId == conversationId);
        if (beforeTicks.HasValue) query = query.Where(x => x.PhysicalTicks < beforeTicks.Value);
        var page = await query.OrderByDescending(x => x.PhysicalTicks).ThenByDescending(x => x.LogicalCounter)
            .ThenByDescending(x => x.OriginNodeId).ThenByDescending(x => x.Id).Take(Math.Max(1, limit)).ToListAsync(ct);
        page.Reverse(); return page;
    }
    public Task<Message> SaveOutgoingAsync(Guid recipientId, string text, CancellationToken ct = default) => WriteAsync(async db =>
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) throw new ArgumentException("Nội dung tin nhắn không hợp lệ.");
        var s = await db.Settings.SingleAsync(ct);
        var conversation = await EnsureDirectAsync(db, s.UserId, recipientId, ct);
        (s.PhysicalTicks, s.LogicalCounter) = HybridClock.Advance(s.PhysicalTicks, s.LogicalCounter);
        var message = new Message { Id = Guid.CreateVersion7(), ClientMessageId = Guid.CreateVersion7(), ConversationId = conversation,
            SenderUserId = s.UserId, RecipientUserId = recipientId, OriginNodeId = s.NodeId, OriginSequence = ++s.LastSequence,
            Kind = MessageKind.Text, Text = text, CreatedAtTicks = DateTime.UtcNow.Ticks, PhysicalTicks = s.PhysicalTicks,
            LogicalCounter = s.LogicalCounter, Status = MessageStatus.LocalPending, IsRead = true };
        db.Messages.Add(message); return message;
    }, ct);

    public async Task ReceiveAsync(ChatPushDto push, string address, CancellationToken ct = default)
    {
        await WriteAsync(async db =>
        {
            var s = await db.Settings.SingleAsync(ct);
            var m = push.Message;
            if (m.Id == Guid.Empty || m.ClientMessageId == Guid.Empty || m.OriginNodeId != push.Sender.NodeId ||
                m.SenderUserId != push.Sender.User.Id || m.RecipientUserId != s.UserId || m.SenderUserId == s.UserId ||
                m.ConversationId != DirectConversation.GetId(options.ClusterId, m.SenderUserId, s.UserId) || m.OriginSequence < 1 ||
                m.Kind != MessageKind.Text || string.IsNullOrWhiteSpace(m.Text) || m.Text.Length > 16000 ||
                m.PhysicalTicks < DateTime.MinValue.Ticks || m.PhysicalTicks > DateTime.UtcNow.AddDays(1).Ticks ||
                m.LogicalCounter < 0 || m.LogicalCounter > 1000000 || m.CreatedAtTicks < 1 || m.CreatedAtTicks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("Tin nhắn không hợp lệ.");
            var existing = await db.Messages.SingleOrDefaultAsync(x => x.Id == m.Id || x.ClientMessageId == m.ClientMessageId ||
                (x.OriginNodeId == m.OriginNodeId && x.OriginSequence == m.OriginSequence), ct);
            if (existing is not null)
            {
                if (existing.Id != m.Id || existing.ClientMessageId != m.ClientMessageId || existing.OriginNodeId != m.OriginNodeId ||
                    existing.OriginSequence != m.OriginSequence || existing.ConversationId != m.ConversationId || existing.Text != m.Text)
                    throw new InvalidDataException("Xung đột định danh tin nhắn.");
                return true;
            }
            await UpsertPeerAsync(db, push.Sender, address, ct);
            // Persist newly discovered user inside the same transaction before validating the conversation.
            await db.SaveChangesAsync(ct);
            await EnsureDirectAsync(db, s.UserId, m.SenderUserId, ct);
            (s.PhysicalTicks, s.LogicalCounter) = HybridClock.Advance(s.PhysicalTicks, s.LogicalCounter, m.PhysicalTicks, m.LogicalCounter);
            db.Messages.Add(new Message { Id = m.Id, ClientMessageId = m.ClientMessageId, ConversationId = m.ConversationId,
                SenderUserId = m.SenderUserId, RecipientUserId = m.RecipientUserId, OriginNodeId = m.OriginNodeId,
                OriginSequence = m.OriginSequence, Kind = m.Kind, Text = m.Text, CreatedAtTicks = m.CreatedAtTicks,
                PhysicalTicks = m.PhysicalTicks, LogicalCounter = m.LogicalCounter, Status = MessageStatus.Delivered });
            return true;
        }, ct);
    }
    public async Task<IReadOnlyList<Message>> GetPendingAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow.Ticks;
        return await db.Messages.AsNoTracking().Where(x => x.Status == MessageStatus.LocalPending && x.NextRetryTicks <= now)
            .OrderBy(x => x.OriginSequence).Take(50).ToListAsync(ct);
    }
    public async Task SetDeliveryAsync(Guid id, bool delivered, CancellationToken ct = default)
    {
        await WriteAsync(async db =>
        {
            var m = await db.Messages.SingleAsync(x => x.Id == id, ct);
            if (delivered) { m.Status = MessageStatus.Delivered; m.LastError = null; }
            else
            {
                m.RetryCount++;
                m.NextRetryTicks = DateTime.UtcNow.AddSeconds(Math.Min(30, Math.Pow(2, Math.Min(m.RetryCount, 5)))).Ticks;
                m.LastError = "Chưa kết nối được người nhận; sẽ tự thử lại.";
            }
            return true;
        }, ct);
    }
    public async Task SetConversationAsync(Guid id, bool? pin = null, bool? mute = null, bool markRead = false, CancellationToken ct = default)
    {
        await WriteAsync(async db =>
        {
            var c = await db.Conversations.SingleAsync(x => x.Id == id, ct);
            if (pin.HasValue) c.IsPinned = pin.Value;
            if (mute.HasValue) c.IsMuted = mute.Value;
            if (markRead)
            {
                c.LastReadTicks = DateTime.UtcNow.Ticks;
                await db.Messages.Where(x => x.ConversationId == id && !x.IsRead).ExecuteUpdateAsync(x => x.SetProperty(m => m.IsRead, true), ct);
            }
            return true;
        }, ct);
    }
    public async Task<int> GetPendingCountAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Messages.CountAsync(x => x.Status == MessageStatus.LocalPending, ct);
    }
    public void Dispose() => _write.Dispose();
}
