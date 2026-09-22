using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;
using Microsoft.EntityFrameworkCore;

namespace LanChat.Infrastructure;

public sealed partial class ChatStore
{
    private async Task ProjectEntityAsync(ChatDbContext db, Guid entityId, CancellationToken ct)
    {
        var events = await db.Events.Where(x => x.EntityId == entityId).OrderBy(x => x.PhysicalTicks).ThenBy(x => x.LogicalCounter)
            .ThenBy(x => x.OriginNodeId).ThenBy(x => x.Id).ToListAsync(ct);
        var profileEvent = events.LastOrDefault(x => x.Type == ReplicationEventType.UserProfileUpdated && x.ActorUserId == entityId);
        if (profileEvent is not null)
        {
            var p = JsonSerializer.Deserialize<UserProfileDto>(profileEvent.PayloadJson) ?? throw new InvalidDataException();
            if (p.Id != entityId || string.IsNullOrWhiteSpace(p.DisplayName) || p.DisplayName.Length > 100) throw new InvalidDataException("Hồ sơ không hợp lệ.");
            await ProjectProfileAsync(db, p, ct, true);
        }
        var created = events.FirstOrDefault(x => x.Type == ReplicationEventType.MessageCreated);
        if (created is null) return;
        var content = JsonSerializer.Deserialize<MessageContent>(created.PayloadJson) ?? throw new InvalidDataException();
        var source = content.Message;
        if (source.Id != entityId || source.SenderUserId != created.ActorUserId || content.Sender.Id != source.SenderUserId ||
            source.ConversationId != created.ConversationId || source.ClientMessageId == Guid.Empty || source.Text.Length > 16000 || source.OriginSequence < 1 ||
            source.CreatedAtTicks < 1 || source.CreatedAtTicks > DateTime.MaxValue.Ticks ||
            (source.RecipientUserId != Guid.Empty && source.ConversationId != DirectConversation.GetId(options.ClusterId, source.SenderUserId, source.RecipientUserId)))
            throw new InvalidDataException("Nội dung MessageCreated không hợp lệ.");
        await ProjectProfileAsync(db, content.Sender, ct, false);
        if (content.Recipient is not null) await ProjectProfileAsync(db, content.Recipient, ct, false);
        var conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == source.ConversationId, ct);
        if (conversation is null)
        {
            conversation = new Conversation { Id = source.ConversationId, Kind = source.RecipientUserId == Guid.Empty ? ConversationKind.Group : ConversationKind.Direct };
            db.Conversations.Add(conversation);
        }
        if (source.RecipientUserId != Guid.Empty)
            foreach (var user in new[] { source.SenderUserId, source.RecipientUserId })
                if (!await db.Members.AnyAsync(x => x.ConversationId == source.ConversationId && x.UserId == user, ct))
                    db.Members.Add(new ConversationMember { Id = Guid.CreateVersion7(), ConversationId = source.ConversationId, UserId = user });
        var local = await db.Settings.SingleAsync(ct);
        var m = await db.Messages.SingleOrDefaultAsync(x => x.Id == entityId, ct);
        if (m is null)
        {
            m = new Message { Id = source.Id, ClientMessageId = source.ClientMessageId, ConversationId = source.ConversationId,
                SenderUserId = source.SenderUserId, RecipientUserId = source.RecipientUserId, OriginNodeId = source.OriginNodeId,
                OriginSequence = source.OriginSequence, CreatedAtTicks = source.CreatedAtTicks, PhysicalTicks = source.PhysicalTicks,
                LogicalCounter = source.LogicalCounter, Status = source.SenderUserId == local.UserId ? MessageStatus.LocalPending : MessageStatus.Delivered,
                IsRead = source.SenderUserId == local.UserId };
            db.Messages.Add(m);
        }
        else if (m.ClientMessageId != source.ClientMessageId || m.SenderUserId != source.SenderUserId || m.ConversationId != source.ConversationId)
            throw new InvalidDataException("Xung đột MessageCreated.");
        m.Text = source.Text; m.Kind = source.Kind; m.ReplyToMessageId = content.ReplyToMessageId; m.ForwardedFromMessageId = content.ForwardedFromMessageId;
        var edit = events.LastOrDefault(x => x.Type == ReplicationEventType.MessageEdited && x.ActorUserId == m.SenderUserId);
        if (edit is not null)
        {
            var text = JsonSerializer.Deserialize<MessageEdit>(edit.PayloadJson)?.Text ?? "";
            if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) throw new InvalidDataException("Nội dung sửa không hợp lệ.");
            m.Text = text; m.IsEdited = true;
        }
        m.IsRecalled = events.Any(x => x.Type == ReplicationEventType.MessageRecalled && x.ActorUserId == m.SenderUserId);
        if (m.IsRecalled) m.Text = "Tin nhắn đã được thu hồi";
        var audience = content.Audience ?? new[] { source.SenderUserId, source.RecipientUserId };
        var reactions = events.Where(x => x.Type == ReplicationEventType.ReactionChanged && audience.Contains(x.ActorUserId))
            .Select(x => (Event: x, Change: JsonSerializer.Deserialize<ReactionChange>(x.PayloadJson)!))
            .GroupBy(x => (x.Event.ActorUserId, x.Change.Emoji)).Select(x => x.Last()).Where(x => x.Change.Active && !m.IsRecalled).ToArray();
        var existingReactions = await db.Reactions.Where(x => x.MessageId == m.Id).ToListAsync(ct);
        foreach (var row in existingReactions)
            if (!reactions.Any(x => x.Event.ActorUserId == row.UserId && x.Change.Emoji == row.Emoji)) db.Reactions.Remove(row);
        foreach (var reaction in reactions)
        {
            if (!AllowedReactions.Contains(reaction.Change.Emoji)) throw new InvalidDataException("Reaction không hợp lệ.");
            if (!existingReactions.Any(x => x.UserId == reaction.Event.ActorUserId && x.Emoji == reaction.Change.Emoji))
                db.Reactions.Add(new MessageReaction { Id = Guid.CreateVersion7(), MessageId = m.Id, UserId = reaction.Event.ActorUserId, Emoji = reaction.Change.Emoji });
        }
        foreach (var receipt in events.Where(x => x.Type == ReplicationEventType.ReceiptUpdated && audience.Contains(x.ActorUserId)).GroupBy(x => x.ActorUserId))
        {
            var values = receipt.Select(x => JsonSerializer.Deserialize<ReceiptChange>(x.PayloadJson)!).ToArray();
            var row = await db.Receipts.SingleOrDefaultAsync(x => x.MessageId == m.Id && x.UserId == receipt.Key, ct);
            if (row is null) { row = new MessageReceipt { Id = Guid.CreateVersion7(), MessageId = m.Id, UserId = receipt.Key }; db.Receipts.Add(row); }
            row.IsRead |= values.Any(x => x.IsRead); row.ReceivedAtTicks = values.Min(x => x.ReceivedAtTicks);
            if (row.UserId == local.UserId && row.IsRead) m.IsRead = true;
            if (m.SenderUserId == local.UserId && row.UserId != local.UserId) m.Status = MessageStatus.Delivered;
        }
        foreach (var file in content.Attachments ?? [])
        {
            if (file.Id == Guid.Empty || file.FileSize < 0 || file.FileSize > 20L * 1024 * 1024 * 1024 || file.Sha256.Length != 64 ||
                !file.Sha256.All(Uri.IsHexDigit) || file.FileName.Length > 255 || Path.GetFileName(file.FileName) != file.FileName)
                throw new InvalidDataException("Metadata file không hợp lệ.");
            var existing = await db.Attachments.SingleOrDefaultAsync(x => x.Id == file.Id, ct);
            if (existing is null) db.Attachments.Add(new Attachment { Id = file.Id, MessageId = m.Id, FileName = file.FileName, MimeType = file.MimeType,
                FileSize = file.FileSize, Sha256 = file.Sha256.ToLowerInvariant() });
            else if (existing.MessageId != m.Id || existing.Sha256 != file.Sha256.ToLowerInvariant()) throw new InvalidDataException("Xung đột attachment.");
        }
        await db.SaveChangesAsync(ct);
    }
    private static async Task ProjectProfileAsync(ChatDbContext db, UserProfileDto profile, CancellationToken ct, bool overwrite)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == profile.Id, ct);
        if (user is null) { user = new LocalUserProfile { Id = profile.Id }; db.Users.Add(user); }
        else if (!overwrite) return;
        user.DisplayName = profile.DisplayName; user.FullName = profile.FullName; user.Nickname = profile.Nickname; user.Department = profile.Department;
        user.Position = profile.Position; user.Phone = profile.Phone; user.StatusMessage = profile.StatusMessage;
        await db.SaveChangesAsync(ct);
    }
    private static readonly string[] AllowedReactions = ["👍", "❤️", "😂", "😮", "😢"];

    public Task<Message> SendToConversationAsync(Guid conversationId, string text, Guid? replyTo = null, Guid? forwardedFrom = null,
        AttachmentDto[]? attachments = null, CancellationToken ct = default) => WriteAsync(async db =>
    {
        if (text.Length > 16000 || (string.IsNullOrWhiteSpace(text) && (attachments is null || attachments.Length == 0)) || attachments?.Length > 20)
            throw new ArgumentException("Nhập nội dung hoặc chọn tối đa 20 file.");
        var s = await db.Settings.SingleAsync(ct);
        var conversation = await db.Conversations.SingleAsync(x => x.Id == conversationId, ct);
        if (!await db.Members.AnyAsync(x => x.ConversationId == conversationId && x.UserId == s.UserId, ct)) throw new InvalidOperationException("Bạn không còn là thành viên cuộc trò chuyện.");
        if (conversation.Kind == ConversationKind.Group && await db.Groups.AnyAsync(x => x.Id == conversationId && x.IsDeleted, ct)) throw new InvalidOperationException("Nhóm đã bị xóa.");
        if (replyTo.HasValue && !await db.Messages.AnyAsync(x => x.Id == replyTo && x.ConversationId == conversationId, ct)) throw new ArgumentException("Tin được trả lời không thuộc cuộc trò chuyện.");
        var other = conversation.Kind == ConversationKind.Direct ? await db.Members.Where(x => x.ConversationId == conversationId && x.UserId != s.UserId).Select(x => x.UserId).SingleAsync(ct) : Guid.Empty;
        (s.PhysicalTicks, s.LogicalCounter) = HybridClock.Advance(s.PhysicalTicks, s.LogicalCounter);
        var message = new Message { Id = Guid.CreateVersion7(), ClientMessageId = Guid.CreateVersion7(), ConversationId = conversationId,
            SenderUserId = s.UserId, RecipientUserId = other, OriginNodeId = s.NodeId, OriginSequence = ++s.LastSequence,
            Text = text.Trim(), Kind = attachments?.Length > 0 ? attachments.All(x => x.MimeType.StartsWith("image/")) ? MessageKind.Image : MessageKind.File : MessageKind.Text,
            CreatedAtTicks = DateTime.UtcNow.Ticks, PhysicalTicks = s.PhysicalTicks, LogicalCounter = s.LogicalCounter, IsRead = true,
            ReplyToMessageId = replyTo, ForwardedFromMessageId = forwardedFrom, Status = MessageStatus.LocalPending };
        db.Messages.Add(message); await db.SaveChangesAsync(ct);
        await EmitMessageAsync(db, s, message, attachments?.Select(x => x with { MessageId = message.Id }).ToArray(), ct); return message;
    }, ct);
    public Task EditMessageAsync(Guid messageId, string text, CancellationToken ct = default) => MutateMessageAsync(messageId,
        ReplicationEventType.MessageEdited, new MessageEdit(text.Trim()), true, ct);
    public Task RecallMessageAsync(Guid messageId, CancellationToken ct = default) => MutateMessageAsync(messageId, ReplicationEventType.MessageRecalled, new { }, true, ct);
    public Task ReactAsync(Guid messageId, string emoji, bool active, CancellationToken ct = default)
    {
        if (!AllowedReactions.Contains(emoji)) throw new ArgumentException("Reaction không hỗ trợ.");
        return MutateMessageAsync(messageId, ReplicationEventType.ReactionChanged, new ReactionChange(emoji, active), false, ct);
    }
    private async Task MutateMessageAsync<T>(Guid id, ReplicationEventType type, T payload, bool ownOnly, CancellationToken ct)
    {
        await WriteAsync(async db =>
        {
            var s = await db.Settings.SingleAsync(ct); var m = await db.Messages.SingleAsync(x => x.Id == id, ct);
            if ((ownOnly && m.SenderUserId != s.UserId) || m.IsRecalled || !await db.Members.AnyAsync(x => x.ConversationId == m.ConversationId && x.UserId == s.UserId, ct))
                throw new InvalidOperationException("Không được phép thực hiện thao tác với tin nhắn này.");
            await EmitAsync(db, s, type, m.Id, m.ConversationId, payload, ct); return true;
        }, ct);
    }
    public async Task DeleteLocalAsync(Guid messageId, CancellationToken ct = default) => await WriteAsync(async db =>
    { var m = await db.Messages.SingleAsync(x => x.Id == messageId, ct); m.IsDeletedLocal = true; return true; }, ct);
    public async Task<MessageDetails> GetMessageDetailsAsync(Guid messageId, CancellationToken ct = default)
        => (await GetMessageDetailsBatchAsync([messageId], ct)).Single();
    public async Task<MessageDetails[]> GetMessageDetailsBatchAsync(Guid[] messageIds, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var messages = await db.Messages.AsNoTracking().Where(x => messageIds.Contains(x.Id)).ToArrayAsync(ct);
        var senders = messages.Select(x => x.SenderUserId).Distinct().ToArray();
        var names = await db.Users.Where(x => senders.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var attachments = (await db.Attachments.AsNoTracking().Where(x => messageIds.Contains(x.MessageId)).ToArrayAsync(ct)).Select(ToAttachment).ToLookup(x => x.MessageId);
        var reactions = (await db.Reactions.AsNoTracking().Where(x => messageIds.Contains(x.MessageId)).ToArrayAsync(ct)).ToLookup(x => x.MessageId);
        var receipts = (await db.Receipts.AsNoTracking().Where(x => messageIds.Contains(x.MessageId)).ToArrayAsync(ct)).ToLookup(x => x.MessageId);
        var replyIds = messages.Where(x => x.ReplyToMessageId.HasValue).Select(x => x.ReplyToMessageId!.Value).ToArray();
        var replies = await db.Messages.AsNoTracking().Where(x => replyIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        return messages.Select(m => new MessageDetails(m, m.IsRecalled ? [] : attachments[m.Id].ToArray(), reactions[m.Id].Select(x => x.Emoji).ToArray(),
            receipts[m.Id].Count(x => x.UserId != m.SenderUserId), receipts[m.Id].Count(x => x.IsRead && x.UserId != m.SenderUserId),
            m.ReplyToMessageId.HasValue && replies.TryGetValue(m.ReplyToMessageId.Value, out var reply) ? reply.IsDeletedLocal ? "Tin nhắn đã xóa phía bạn" : reply.Text : null, names.GetValueOrDefault(m.SenderUserId))).ToArray();
    }
    private static AttachmentDto ToAttachment(Attachment x) => new(x.Id, x.MessageId, x.FileName, x.MimeType, x.FileSize, x.Sha256);
    public async Task<Message[]> GetMessageWindowAsync(Guid conversationId, Guid anchorId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Messages.FromSqlInterpolated($"WITH ordered AS (SELECT Id,ROW_NUMBER() OVER(ORDER BY PhysicalTicks,LogicalCounter,OriginNodeId,Id) AS n FROM Messages WHERE ConversationId={conversationId} AND IsDeletedLocal=0), anchor AS (SELECT n FROM ordered WHERE Id={anchorId}) SELECT m.* FROM Messages m JOIN ordered o ON m.Id=o.Id WHERE o.n BETWEEN (SELECT n FROM anchor)-50 AND (SELECT n FROM anchor)+50 ORDER BY o.n").AsNoTracking().ToArrayAsync(ct);
    }
    public async Task<AttachmentDto[]> GetAttachmentsAsync(Guid? conversationId = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var local = (await db.Settings.SingleAsync(ct)).UserId;
        var files = await (from a in db.Attachments join m in db.Messages on a.MessageId equals m.Id
            where !m.IsDeletedLocal && !m.IsRecalled && (!conversationId.HasValue || m.ConversationId == conversationId) &&
                db.Members.Any(c => c.ConversationId == m.ConversationId && c.UserId == local) select a).AsNoTracking().ToArrayAsync(ct);
        return files.Select(ToAttachment).ToArray();
    }
    public async Task<GroupSummary[]> GetGroupsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var local = (await db.Settings.SingleAsync(ct)).UserId;
        var groups = await db.Groups.AsNoTracking().Where(x => !x.IsDeleted && db.GroupMembers.Any(m => m.GroupId == x.Id && m.UserId == local)).ToArrayAsync(ct);
        var members = await db.GroupMembers.AsNoTracking().ToArrayAsync(ct);
        return groups.Select(x => new GroupSummary(x.Id, x.Name, x.OwnerUserId, members.Where(m => m.GroupId == x.Id).Select(m => m.UserId).ToArray(), x.IsDeleted, x.AvatarBase64)).ToArray();
    }
    public async Task<SearchResult[]> SearchMessagesAsync(string text, Guid? conversationId = null, CancellationToken ct = default)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(12).Select(w => "\"" + w.Replace("\"", "\"\"") + "\"*").ToArray();
        if (words.Length == 0) return [];
        await using var db = await factory.CreateDbContextAsync(ct); var local = (await db.Settings.SingleAsync(ct)).UserId;
        await db.Database.OpenConnectionAsync(ct); await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT m.Id,m.ConversationId,m.Text,m.CreatedAtTicks FROM MessageSearch s JOIN Messages m ON s.Id=m.Id WHERE MessageSearch MATCH $q AND m.IsDeletedLocal=0 AND m.IsRecalled=0 AND EXISTS(SELECT 1 FROM ConversationMembers cm WHERE cm.ConversationId=m.ConversationId AND cm.UserId=$u)" +
            (conversationId.HasValue ? " AND m.ConversationId=$c" : "") + " ORDER BY rank LIMIT 100";
        void Parameter(string name, object value) { var p = command.CreateParameter(); p.ParameterName = name; p.Value = value; command.Parameters.Add(p); }
        Parameter("$q", string.Join(" AND ", words)); Parameter("$u", local.ToString().ToUpperInvariant());
        if (conversationId.HasValue) Parameter("$c", conversationId.Value.ToString().ToUpperInvariant());
        var rows = new List<SearchResult>(); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetInt64(3)));
        return rows.ToArray();
    }
}
