using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;

namespace LanChat.Infrastructure;

public sealed partial class ChatStore
{
    private async Task ProjectEntityAsync(ChatDbContext db, Guid entityId, CancellationToken ct)
    {
        var events = db.Rows<ReplicationEvent>("EntityId=$0 ORDER BY PhysicalTicks,LogicalCounter,OriginNodeId,Id", entityId);
        var profileEvent = events.LastOrDefault(x => x.Type == ReplicationEventType.UserProfileUpdated && x.ActorUserId == entityId);
        if (profileEvent is not null)
        {
            var p = JsonSerializer.Deserialize<UserProfileDto>(profileEvent.PayloadJson) ?? throw new InvalidDataException();
            if (p.Id != entityId || string.IsNullOrWhiteSpace(p.DisplayName) || p.DisplayName.Length > 100) throw new InvalidDataException("Hồ sơ không hợp lệ.");
            await ProjectProfileAsync(db, p, ct, true);
        }
        var created = events.FirstOrDefault(x => x.Type == ReplicationEventType.MessageCreated);
        if (created is null) return;
        var local = db.Required<LocalSettings>();
        var envelope = DeviceIdentity.ParseEnvelope(created.PayloadJson);
        var content = ReadEncryptedEvent<MessageContent>(db, created, local.UserId);
        if (content is null) return;
        var source = content.Message;
        var mentionAudience = envelope.Recipients.Select(x => x.UserId).ToArray();
        var audience = envelope.Recipients.Select(x => x.UserId).ToArray();
        if (source.Id != entityId || source.SenderUserId != created.ActorUserId || content.Sender.Id != source.SenderUserId ||
            source.OriginNodeId != created.OriginNodeId || source.ConversationId != created.ConversationId ||
            source.ClientMessageId == Guid.Empty || source.Text.Length > 16000 || source.OriginSequence < 1 || !audience.Contains(source.SenderUserId) ||
            source.CreatedAtTicks < 1 || source.CreatedAtTicks > DateTime.MaxValue.Ticks ||
            (source.RecipientUserId != Guid.Empty && (audience.Length != 2 || !audience.Contains(source.RecipientUserId) ||
                source.ConversationId != DirectConversation.GetId(options.ClusterId, source.SenderUserId, source.RecipientUserId))))
            throw new InvalidDataException("Nội dung MessageCreated không hợp lệ.");
        if (!audience.Contains(local.UserId) && source.RecipientUserId != Guid.Empty)
            throw new InvalidDataException("Khóa lịch sử chỉ áp dụng cho tin nhắn nhóm.");
        if (source.RecipientUserId == Guid.Empty && db.Exists<Group>("Id=$0", source.ConversationId))
            audience = audience.Concat(db.Rows<GroupMember>("GroupId=$0", source.ConversationId).Select(x => x.UserId)).Distinct().ToArray();
        await ProjectProfileAsync(db, content.Sender, ct, false);
        if (content.Recipient is not null) await ProjectProfileAsync(db, content.Recipient, ct, false);
        var conversation = db.One<Conversation>("Id=$0", source.ConversationId);
        if (conversation is null)
        {
            conversation = new Conversation { Id = source.ConversationId, Kind = source.RecipientUserId == Guid.Empty ? ConversationKind.Group : ConversationKind.Direct };
            db.Insert(conversation);
        }
        if (source.RecipientUserId != Guid.Empty)
            foreach (var user in new[] { source.SenderUserId, source.RecipientUserId })
                if (!db.Exists<ConversationMember>("ConversationId=$0 AND UserId=$1", source.ConversationId, user))
                    db.Insert(new ConversationMember { Id = LanChat.Domain.AppIdentity.NewId(), ConversationId = source.ConversationId, UserId = user });
        var m = db.One<Message>("Id=$0", entityId);
        if (m is null)
        {
            if (db.Exists<ConversationMember>("ConversationId=$0 AND UserId=$1", source.ConversationId, local.UserId))
                conversation.IsHiddenLocal = false;
            m = new Message { Id = source.Id, ClientMessageId = source.ClientMessageId, ConversationId = source.ConversationId,
                SenderUserId = source.SenderUserId, RecipientUserId = source.RecipientUserId, OriginNodeId = source.OriginNodeId,
                OriginSequence = source.OriginSequence, CreatedAtTicks = source.CreatedAtTicks, PhysicalTicks = source.PhysicalTicks,
                LogicalCounter = source.LogicalCounter, Status = source.SenderUserId == local.UserId ? MessageStatus.LocalPending : MessageStatus.Delivered,
                IsRead = source.SenderUserId == local.UserId };
            db.Insert(m);
        }
        else if (m.ClientMessageId != source.ClientMessageId || m.SenderUserId != source.SenderUserId || m.ConversationId != source.ConversationId)
            throw new InvalidDataException("Xung đột MessageCreated.");
        m.Text = source.Text; m.Kind = source.Kind; m.ReplyToMessageId = content.ReplyToMessageId; m.ForwardedFromMessageId = content.ForwardedFromMessageId;
        var edits = events.Where(x => x.Type == ReplicationEventType.MessageEdited && x.ActorUserId == m.SenderUserId && x.ConversationId == m.ConversationId);
        var latestEdit = edits.Reverse().Select(x => (Event: x, Value: ReadEncryptedEvent<MessageEdit>(db, x, local.UserId))).FirstOrDefault(x => x.Value is not null);
        var edit = latestEdit.Value;
        if (edit is not null) mentionAudience = DeviceIdentity.ParseEnvelope(latestEdit.Event.PayloadJson).Recipients.Select(x => x.UserId).ToArray();
        if (edit is not null)
        {
            var text = edit.Text;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) throw new InvalidDataException("Nội dung sửa không hợp lệ.");
            m.Text = text; m.IsEdited = true;
        }
        m.IsRecalled = events.Any(x => x.Type == ReplicationEventType.MessageRecalled && x.ActorUserId == m.SenderUserId &&
            x.ConversationId == m.ConversationId && ReadEncryptedEvent<object>(db, x, local.UserId) is not null);
        if (m.IsRecalled) m.Text = "Tin nhắn đã được thu hồi";
        var mentionValues = m.IsRecalled || m.IsDeletedLocal ? [] : Mentions.Normalize(m.Text, edit?.Mentions ?? (edit is null ? content.Mentions : null),
            mentionAudience, source.RecipientUserId == Guid.Empty);
        var projected = db.Rows<MessageMentionProjection>("MessageId=$0", m.Id);
        var desired = mentionValues.SelectMany(value => value.UserIds.Select(user => (value.Start, value.Length, value.All, User: user))).ToArray();
        foreach (var row in projected)
            if (!desired.Any(value => value.Start == row.Start && value.Length == row.Length && value.All == row.IsAll && value.User == row.UserId))
                db.Delete<MessageMentionProjection>("Id=$0", row.Id);
        foreach (var value in desired)
            if (!projected.Any(row => row.Start == value.Start && row.Length == value.Length && row.IsAll == value.All && row.UserId == value.User))
                db.Insert(new MessageMentionProjection { Id = Guid.NewGuid(), MessageId = m.Id, UserId = value.User, Start = value.Start, Length = value.Length, IsAll = value.All });
        var reactions = events.Where(x => x.Type == ReplicationEventType.ReactionChanged && x.ConversationId == m.ConversationId && audience.Contains(x.ActorUserId))
            .Select(x => (Event: x, Change: ReadEncryptedEvent<ReactionChange>(db, x, local.UserId))).Where(x => x.Change is not null)
            .Select(x => (x.Event, Change: x.Change!)).GroupBy(x => (x.Event.ActorUserId, x.Change.Emoji)).Select(x => x.Last())
            .Where(x => x.Change.Active && !m.IsRecalled).ToArray();
        var existingReactions = db.Rows<MessageReaction>("MessageId=$0", m.Id);
        foreach (var row in existingReactions)
            if (!reactions.Any(x => x.Event.ActorUserId == row.UserId && x.Change.Emoji == row.Emoji)) db.Delete<MessageReaction>("Id=$0", row.Id);
        foreach (var reaction in reactions)
        {
            if (!AllowedReactions.Contains(reaction.Change.Emoji)) throw new InvalidDataException("Reaction không hợp lệ.");
            if (!existingReactions.Any(x => x.UserId == reaction.Event.ActorUserId && x.Emoji == reaction.Change.Emoji))
                db.Insert(new MessageReaction { Id = LanChat.Domain.AppIdentity.NewId(), MessageId = m.Id, UserId = reaction.Event.ActorUserId, Emoji = reaction.Change.Emoji });
        }
        foreach (var receipt in events.Where(x => x.Type == ReplicationEventType.ReceiptUpdated && x.ConversationId == m.ConversationId && audience.Contains(x.ActorUserId)).GroupBy(x => x.ActorUserId))
        {
            var values = receipt.Select(x => ReadEncryptedEvent<ReceiptChange>(db, x, local.UserId)).OfType<ReceiptChange>().ToArray();
            if (values.Length == 0) continue;
            var row = db.One<MessageReceipt>("MessageId=$0 AND UserId=$1", m.Id, receipt.Key);
            if (row is null) { row = new MessageReceipt { Id = LanChat.Domain.AppIdentity.NewId(), MessageId = m.Id, UserId = receipt.Key }; db.Insert(row); }
            row.IsRead |= values.Any(x => x.IsRead); row.ReceivedAtTicks = values.Min(x => x.ReceivedAtTicks);
            if (row.UserId == local.UserId && row.IsRead) m.IsRead = true;
            if (m.SenderUserId == local.UserId && row.UserId != local.UserId) m.Status = MessageStatus.Delivered;
        }
        foreach (var file in content.Attachments ?? [])
        {
            if (file.Id == Guid.Empty || file.FileSize < 0 || file.FileSize > 20L * 1024 * 1024 * 1024 || file.Sha256.Length != 64 ||
                !file.Sha256.All(Uri.IsHexDigit) || file.FileName.Length > 255 || Path.GetFileName(file.FileName) != file.FileName)
                throw new InvalidDataException("Metadata file không hợp lệ.");
            var existing = db.One<Attachment>("Id=$0", file.Id);
            var hash = file.Sha256.ToLowerInvariant();
            if (existing is null)
            {
                var fileKey = DeviceIdentity.Decode(file.FileKey, DeviceIdentity.KeyBytes);
                Array.Clear(fileKey, 0, fileKey.Length);
                db.Insert(new Attachment { Id = file.Id, MessageId = m.Id, FileName = file.FileName, MimeType = file.MimeType,
                    FileSize = file.FileSize, Sha256 = hash, FileKey = file.FileKey! });
            }
            else if (existing.MessageId != m.Id || existing.Sha256 != hash || existing.FileKey != file.FileKey) throw new InvalidDataException("Xung đột attachment.");
        }
        db.SaveChanges();
    }
    private static async Task ProjectProfileAsync(ChatDbContext db, UserProfileDto profile, CancellationToken ct, bool overwrite)
    {
        var user = db.One<LocalUserProfile>("Id=$0", profile.Id);
        if (user is null) { user = new LocalUserProfile { Id = profile.Id }; db.Insert(user); }
        else if (!overwrite) return;
        user.DisplayName = profile.DisplayName; user.FullName = profile.FullName; user.Nickname = profile.Nickname; user.Department = profile.Department;
        user.Position = profile.Position; user.Phone = profile.Phone; user.StatusMessage = profile.StatusMessage;
        db.SaveChanges();
    }
    private static readonly string[] AllowedReactions = ["👍", "❤️", "😂", "😮", "😢"];

    public Task<Message> SendToConversationAsync(Guid conversationId, string text, Guid? replyTo = null, Guid? forwardedFrom = null,
        AttachmentDto[]? attachments = null, CancellationToken ct = default, MessageMention[]? mentions = null) => WriteAsync(async db =>
    {
        if (text.Length > 16000 || (string.IsNullOrWhiteSpace(text) && (attachments is null || attachments.Length == 0)) || attachments?.Length > 20)
            throw new ArgumentException("Nhập nội dung hoặc chọn tối đa 20 file.");
        var s = db.Required<LocalSettings>();
        var conversation = db.Required<Conversation>("Id=$0", conversationId);
        if (!db.Exists<ConversationMember>("ConversationId=$0 AND UserId=$1", conversationId, s.UserId)) throw new InvalidOperationException("Bạn không còn là thành viên cuộc trò chuyện.");
        if (conversation.Kind == ConversationKind.Group && db.Exists<Group>("Id=$0 AND IsDeleted=1", conversationId)) throw new InvalidOperationException("Nhóm đã bị xóa.");
        if (replyTo.HasValue && !db.Exists<Message>("Id=$0 AND ConversationId=$1", replyTo, conversationId)) throw new ArgumentException("Tin được trả lời không thuộc cuộc trò chuyện.");
        var normalizedMentions = Mentions.Normalize(text, mentions, db.Rows<ConversationMember>("ConversationId=$0", conversationId).Select(x => x.UserId).ToArray(), conversation.Kind == ConversationKind.Group, trim: true);
        conversation.IsHiddenLocal = false;
        var other = conversation.Kind == ConversationKind.Direct ? db.Required<ConversationMember>("ConversationId=$0 AND UserId<>$1", conversationId, s.UserId).UserId : Guid.Empty;
        (s.PhysicalTicks, s.LogicalCounter) = HybridClock.Advance(s.PhysicalTicks, s.LogicalCounter);
        var message = new Message { Id = LanChat.Domain.AppIdentity.NewId(), ClientMessageId = LanChat.Domain.AppIdentity.NewId(), ConversationId = conversationId,
            SenderUserId = s.UserId, RecipientUserId = other, OriginNodeId = s.NodeId, OriginSequence = ++s.LastSequence,
            Text = text.Trim(), Kind = attachments?.Length > 0 ? attachments.All(x => x.MimeType.StartsWith("image/")) ? MessageKind.Image : MessageKind.File : MessageKind.Text,
            CreatedAtTicks = DateTime.UtcNow.Ticks, PhysicalTicks = s.PhysicalTicks, LogicalCounter = s.LogicalCounter, IsRead = true,
            ReplyToMessageId = replyTo, ForwardedFromMessageId = forwardedFrom, Status = MessageStatus.LocalPending };
        db.Insert(message); db.SaveChanges();
        await EmitMessageAsync(db, s, message, attachments?.Select(x => x with { MessageId = message.Id }).ToArray(), ct, normalizedMentions); return message;
    }, ct);
    public Task EditMessageAsync(Guid messageId, string text, CancellationToken ct = default, MessageMention[]? mentions = null) => WriteAsync(async db =>
    {
        var s = db.Required<LocalSettings>(); var m = db.Required<Message>("Id=$0", messageId);
        if (m.SenderUserId != s.UserId || m.IsRecalled || m.IsDeletedLocal || string.IsNullOrWhiteSpace(text) || text.Length > 16000 ||
            !db.Exists<ConversationMember>("ConversationId=$0 AND UserId=$1", m.ConversationId, s.UserId)) throw new InvalidOperationException("Không được phép sửa tin nhắn này.");
        var audience = db.Rows<ConversationMember>("ConversationId=$0", m.ConversationId).Select(x => x.UserId).ToArray();
        var normalized = Mentions.Normalize(text, mentions, audience, m.RecipientUserId == Guid.Empty, trim: true, resolveAll: false);
        await EmitAsync(db, s, ReplicationEventType.MessageEdited, m.Id, m.ConversationId, new MessageEdit(text.Trim(), normalized), ct);
        return true;
    }, ct);
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
            var s = db.Required<LocalSettings>(); var m = db.Required<Message>("Id=$0", id);
            if ((ownOnly && m.SenderUserId != s.UserId) || m.IsRecalled || !db.Exists<ConversationMember>("ConversationId=$0 AND UserId=$1", m.ConversationId, s.UserId))
                throw new InvalidOperationException("Không được phép thực hiện thao tác với tin nhắn này.");
            await EmitAsync(db, s, type, m.Id, m.ConversationId, payload, ct); return true;
        }, ct);
    }
    public async Task DeleteLocalAsync(Guid messageId, CancellationToken ct = default) => await WriteAsync(async db =>
    { var m = db.Required<Message>("Id=$0", messageId); m.IsDeletedLocal = true; db.Delete<MessageMentionProjection>("MessageId=$0", messageId); return true; }, ct);
    public async Task<MessageDetails> GetMessageDetailsAsync(Guid messageId, CancellationToken ct = default)
        => (await GetMessageDetailsBatchAsync([messageId], ct)).Single();
    public async Task<MessageDetails[]> GetMessageDetailsBatchAsync(Guid[] messageIds, CancellationToken ct = default)
    {
        if (messageIds.Length == 0) return [];
        if (messageIds.Length > 500)
        {
            var result = new List<MessageDetails>();
            for (var start = 0; start < messageIds.Length; start += 500)
                result.AddRange(await GetMessageDetailsBatchAsync(messageIds.Skip(start).Take(500).ToArray(), ct));
            return result.ToArray();
        }
        using var db = database.Open(ct);
        var messages = db.Rows<Message>(In("Id", messageIds), Args(messageIds));
        var attachments = db.Rows<Attachment>(In("MessageId", messageIds), Args(messageIds)).Select(ToAttachment).ToLookup(x => x.MessageId);
        var mentions = db.Rows<MessageMentionProjection>(In("MessageId", messageIds), Args(messageIds)).ToLookup(x => x.MessageId);
        var reactions = db.Rows<MessageReaction>(In("MessageId", messageIds), Args(messageIds)).ToLookup(x => x.MessageId);
        var receipts = db.Rows<MessageReceipt>(In("MessageId", messageIds), Args(messageIds)).ToLookup(x => x.MessageId);
        var sources = messages.Where(m => !m.IsRecalled && m.ReplyToMessageId.HasValue).Select(m => m.Id).ToArray();
        var replies = sources.Length == 0 ? new Dictionary<Guid, Message>() : db.Query<Message>(
            "SELECT DISTINCT original.* FROM Messages source JOIN Messages original ON original.Id=source.ReplyToMessageId " +
            "AND original.ConversationId=source.ConversationId WHERE " + In("source.Id", sources), Args(sources)).ToDictionary(m => m.Id);
        var senderIds = messages.Concat(replies.Values).Select(x => x.SenderUserId).Distinct().ToArray();
        var profiles = new Dictionary<Guid, LocalUserProfile>();
        for (var offset = 0; offset < senderIds.Length; offset += 500)
        {
            var batch = senderIds.Skip(offset).Take(500).ToArray();
            foreach (var profile in db.Rows<LocalUserProfile>(In("Id", batch), Args(batch))) profiles.Add(profile.Id, profile);
        }
        var attachmentOnly = replies.Values.Where(m => !m.IsRecalled && !m.IsDeletedLocal && string.IsNullOrWhiteSpace(m.Text))
            .Select(m => m.Id).ToArray();
        var quoteFiles = (attachmentOnly.Length == 0 ? Array.Empty<Attachment>() : db.Rows<Attachment>(In("MessageId", attachmentOnly), Args(attachmentOnly)))
            .ToLookup(a => a.MessageId);
        return messages.Select(m =>
        {
            profiles.TryGetValue(m.SenderUserId, out var sender);
            var quote = m.IsRecalled || m.ReplyToMessageId is not Guid replyId ? null : DescribeQuote(m.ConversationId, replyId);
            return new MessageDetails(m, m.IsRecalled ? [] : attachments[m.Id].ToArray(), reactions[m.Id].Select(x => x.Emoji).ToArray(),
                receipts[m.Id].Count(x => x.UserId != m.SenderUserId), receipts[m.Id].Count(x => x.IsRead && x.UserId != m.SenderUserId),
                quote?.Preview, sender?.DisplayName) { SenderDepartment = sender?.Department, Quote = quote, Mentions = m.IsRecalled || m.IsDeletedLocal ? [] : mentions[m.Id].GroupBy(x => (x.Start, x.Length, x.IsAll)).Select(g => new MessageMention(g.Key.Start, g.Key.Length, g.Key.IsAll, g.Select(x => x.UserId).ToArray())).ToArray() };
        }).ToArray();

        ReplyQuote DescribeQuote(Guid conversation, Guid id)
        {
            if (!replies.TryGetValue(id, out var original) || original.ConversationId != conversation)
                return new(id, "Chưa có tin nhắn gốc");
            if (original.IsDeletedLocal) return new(id, "Tin nhắn đã xóa phía bạn");
            if (original.IsRecalled) return new(id, "Tin nhắn đã thu hồi");

            profiles.TryGetValue(original.SenderUserId, out var author);
            var text = original.Text.NormalizeLines(" ").Trim();
            if (text.Length == 0)
            {
                var files = quoteFiles[id];
                text = files.Any(a => a.MimeType == FolderAttachment.MimeType) ? "Thư mục" :
                    original.Kind == MessageKind.Image || files.Any(a => a.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) ? "Ảnh" :
                    files.Any() || original.Kind == MessageKind.File ? "Tệp" : "Tin nhắn";
            }
            if (text.Length > 320) text = text.Substring(0, char.IsHighSurrogate(text[319]) ? 319 : 320).TrimEnd() + "…";
            return new(id, text, author?.DisplayName, author?.Department, CanNavigate: true);
        }
    }
    private static string In(string column, Guid[] ids) => column + " IN (" + ChatDbContext.Parameters(ids.Length) + ")";
    private static object[] Args(Guid[] ids) => ids.Cast<object>().ToArray();
    private static AttachmentDto ToAttachment(Attachment x) => new(x.Id, x.MessageId, x.FileName, x.MimeType, x.FileSize, x.Sha256, x.FileKey);
    public async Task<Message[]> GetMessageWindowAsync(Guid conversationId, Guid anchorId, CancellationToken ct = default)
    {
        using var db = database.Open(ct);
        return db.Query<Message>("WITH ordered AS (SELECT Id,ROW_NUMBER() OVER(ORDER BY PhysicalTicks,LogicalCounter,OriginNodeId,Id) AS n FROM Messages WHERE ConversationId=$0 AND IsDeletedLocal=0), anchor AS (SELECT n FROM ordered WHERE Id=$1) SELECT m.* FROM Messages m JOIN ordered o ON m.Id=o.Id WHERE o.n BETWEEN (SELECT n FROM anchor)-50 AND (SELECT n FROM anchor)+50 ORDER BY o.n", conversationId, anchorId);
    }
    public async Task<AttachmentDto[]> GetAttachmentsAsync(Guid? conversationId = null, CancellationToken ct = default)
    {
        using var db = database.Open(ct); var local = (db.Required<LocalSettings>()).UserId;
        var files = db.Query<Attachment>("SELECT a.* FROM Attachments a JOIN Messages m ON a.MessageId=m.Id WHERE m.IsDeletedLocal=0 AND m.IsRecalled=0 AND ($0 IS NULL OR m.ConversationId=$0) AND EXISTS(SELECT 1 FROM ConversationMembers c WHERE c.ConversationId=m.ConversationId AND c.UserId=$1)", conversationId, local);
        return files.Select(ToAttachment).ToArray();
    }
    public async Task<GroupSummary[]> GetGroupsAsync(CancellationToken ct = default)
    {
        using var db = database.Open(ct); var local = (db.Required<LocalSettings>()).UserId;
        var groups = db.Rows<Group>("IsDeleted=0 AND Id IN (SELECT GroupId FROM GroupMembers WHERE UserId=$0)", local);
        var members = db.Rows<GroupMember>();
        using var query = db.Command("SELECT GroupId FROM PendingPairGroups");
        using var reader = query.ExecuteReader();
        var pending = new HashSet<Guid>();
        while (reader.Read()) pending.Add(reader.GetGuid(0));
        return groups.Select(x => new GroupSummary(x.Id, x.Name, x.OwnerUserId, members.Where(m => m.GroupId == x.Id).Select(m => m.UserId).ToArray(), x.IsDeleted, x.AvatarBase64)
            { PendingCreation = pending.Contains(x.Id) }).ToArray();
    }
    public async Task<SearchResult[]> SearchMessagesAsync(string text, Guid? conversationId = null, CancellationToken ct = default)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(12).Select(w => "\"" + w.Replace("\"", "\"\"") + "\"*").ToArray();
        if (words.Length == 0) return [];
        using var db = database.Open(ct); var local = (db.Required<LocalSettings>()).UserId;
        using var command = db.Command("");
        command.CommandText = "SELECT m.Id,m.ConversationId,m.Text,m.CreatedAtTicks FROM MessageSearch s JOIN Messages m ON s.Id=m.Id WHERE MessageSearch MATCH $q AND m.IsDeletedLocal=0 AND m.IsRecalled=0 AND EXISTS(SELECT 1 FROM ConversationMembers cm WHERE cm.ConversationId=m.ConversationId AND cm.UserId=$u)" +
            (conversationId.HasValue ? " AND m.ConversationId=$c" : "") + " ORDER BY rank LIMIT 100";
        void Parameter(string name, object value) { var p = command.CreateParameter(); p.ParameterName = name; p.Value = value; command.Parameters.Add(p); }
        Parameter("$q", string.Join(" AND ", words)); Parameter("$u", local.ToString().ToUpperInvariant());
        if (conversationId.HasValue) Parameter("$c", conversationId.Value.ToString().ToUpperInvariant());
        var rows = new List<SearchResult>(); using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetInt64(3)));
        return rows.ToArray();
    }
}
