using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;
using Microsoft.EntityFrameworkCore;

namespace LanChat.Infrastructure;

public sealed partial class ConsensusService
{
    private async Task<string?> ValidateGroupCommandAsync(ControlCommand command, CancellationToken ct)
    {
        if (!Enum.IsDefined(command.Type)) return "Thao tác nhóm không hợp lệ.";
        if (command.Id == Guid.Empty || command.GroupId == Guid.Empty || command.ActorUserId == Guid.Empty) return "Định danh thao tác không hợp lệ.";
        if (command.Type is ControlCommandType.GroupCreated or ControlCommandType.GroupRenamed &&
            (string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 100)) return "Tên nhóm cần từ 1 đến 100 ký tự.";
        if (command.AvatarBase64?.Length > 48000) return "Ảnh nhóm quá lớn; chọn ảnh nhỏ hơn 32 KB.";
        await using var db = await factory.CreateDbContextAsync(ct);
        if (command.Type == ControlCommandType.GroupCreated)
        {
            if (await db.Groups.AnyAsync(x => x.Id == command.GroupId, ct)) return "Nhóm đã tồn tại.";
            if (command.Members is null || command.Members.Length > 100 || !command.Members.Contains(command.ActorUserId)) return "Danh sách thành viên không hợp lệ.";
            var known = await db.Users.Where(x => command.Members.Contains(x.Id)).Select(x => x.Id).ToArrayAsync(ct);
            if (command.Members.Except(known).Any()) return "Thông tin thành viên chưa được đồng bộ. Vui lòng thử lại sau.";
            return null;
        }
        var group = await db.Groups.SingleOrDefaultAsync(x => x.Id == command.GroupId && !x.IsDeleted, ct);
        if (group is null) return "Nhóm không tồn tại hoặc đã bị xóa.";
        if (group.OwnerUserId != command.ActorUserId) return "Chỉ chủ nhóm được thay đổi cấu trúc nhóm.";
        if (command.Type is ControlCommandType.GroupMemberAdded or ControlCommandType.GroupMemberRemoved or ControlCommandType.GroupOwnerChanged)
        {
            if (command.TargetUserId is null || command.TargetUserId == Guid.Empty) return "Chưa chọn người dùng.";
            if (!await db.Users.AnyAsync(x => x.Id == command.TargetUserId, ct)) return "Người dùng chưa được đồng bộ.";
            if (command.Type == ControlCommandType.GroupMemberAdded && await db.GroupMembers.CountAsync(x => x.GroupId == group.Id, ct) >= 100) return "Nhóm tối đa 100 thành viên.";
            if (command.Type == ControlCommandType.GroupMemberRemoved && command.TargetUserId == group.OwnerUserId) return "Hãy chuyển quyền chủ nhóm trước khi xóa chủ nhóm.";
            if (command.Type == ControlCommandType.GroupOwnerChanged && !await db.GroupMembers.AnyAsync(x => x.GroupId == group.Id && x.UserId == command.TargetUserId, ct)) return "Chủ nhóm mới phải là thành viên hiện tại.";
        }
        return null;
    }
    private async Task ApplyCommittedAsync(ChatDbContext db, ClusterState state, CancellationToken ct)
    {
        var entries = await db.ControlLog.Where(x => x.Index > state.LastAppliedIndex && x.Index <= state.CommitIndex).OrderBy(x => x.Index).ToArrayAsync(ct);
        foreach (var entry in entries)
        {
            if (entry.Index != state.LastAppliedIndex + 1) throw new InvalidDataException("Thiếu control log trước khi apply.");
            var c = JsonSerializer.Deserialize<ControlCommand>(entry.CommandJson)!;
            if (c.Type >= ControlCommandType.GroupCreated)
            {
                var group = await db.Groups.SingleOrDefaultAsync(x => x.Id == c.GroupId, ct);
                if (c.Type == ControlCommandType.GroupCreated && group is null)
                {
                    group = new Group { Id = c.GroupId, ConversationId = c.GroupId, Name = c.Name!, OwnerUserId = c.ActorUserId, AvatarBase64 = c.AvatarBase64 };
                    db.Groups.Add(group);
                    var conversation = await db.Conversations.SingleOrDefaultAsync(x => x.Id == c.GroupId, ct);
                    if (conversation is null) db.Conversations.Add(new Conversation { Id = c.GroupId, Kind = ConversationKind.Group, Title = c.Name! });
                    else { conversation.Kind = ConversationKind.Group; conversation.Title = c.Name!; }
                    foreach (var user in c.Members!.Distinct())
                    {
                        db.GroupMembers.Add(new GroupMember { Id = Guid.CreateVersion7(), GroupId = c.GroupId, UserId = user });
                        if (!await db.Members.AnyAsync(x => x.ConversationId == c.GroupId && x.UserId == user, ct))
                            db.Members.Add(new ConversationMember { Id = Guid.CreateVersion7(), ConversationId = c.GroupId, UserId = user });
                    }
                }
                else if (group is not null)
                {
                    switch (c.Type)
                    {
                        case ControlCommandType.GroupRenamed:
                            group.Name = c.Name!; (await db.Conversations.SingleAsync(x => x.Id == group.Id, ct)).Title = c.Name!; break;
                        case ControlCommandType.GroupAvatarChanged: group.AvatarBase64 = c.AvatarBase64; break;
                        case ControlCommandType.GroupOwnerChanged: group.OwnerUserId = c.TargetUserId!.Value; break;
                        case ControlCommandType.GroupDeleted: group.IsDeleted = true; break;
                        case ControlCommandType.GroupMemberAdded:
                            if (!await db.GroupMembers.AnyAsync(x => x.GroupId == group.Id && x.UserId == c.TargetUserId, ct))
                            {
                                db.GroupMembers.Add(new GroupMember { Id = Guid.CreateVersion7(), GroupId = group.Id, UserId = c.TargetUserId!.Value });
                                db.Members.Add(new ConversationMember { Id = Guid.CreateVersion7(), ConversationId = group.Id, UserId = c.TargetUserId.Value });
                            }
                            break;
                        case ControlCommandType.GroupMemberRemoved:
                            await db.GroupMembers.Where(x => x.GroupId == group.Id && x.UserId == c.TargetUserId).ExecuteDeleteAsync(ct);
                            await db.Members.Where(x => x.ConversationId == group.Id && x.UserId == c.TargetUserId).ExecuteDeleteAsync(ct); break;
                    }
                }
                if (!await db.Messages.AnyAsync(x => x.Id == c.Id, ct))
                {
                    var text = c.Type switch
                    {
                        ControlCommandType.GroupCreated => $"Đã tạo nhóm {c.Name}", ControlCommandType.GroupRenamed => $"Tên nhóm được đổi thành {c.Name}",
                        ControlCommandType.GroupMemberAdded => "Đã thêm thành viên vào nhóm", ControlCommandType.GroupMemberRemoved => "Đã xóa thành viên khỏi nhóm",
                        ControlCommandType.GroupOwnerChanged => "Đã chuyển quyền chủ nhóm", ControlCommandType.GroupDeleted => "Nhóm đã bị xóa", _ => "Đã cập nhật ảnh nhóm"
                    };
                    db.Messages.Add(new Message { Id = c.Id, ClientMessageId = c.Id, ConversationId = c.GroupId, SenderUserId = Guid.Empty,
                        OriginNodeId = DirectConversation.GetId(options.ClusterId, Guid.Empty, Guid.Empty), OriginSequence = entry.Index,
                        Text = text, Kind = MessageKind.System, CreatedAtTicks = c.CreatedAtTicks, PhysicalTicks = c.CreatedAtTicks, Status = MessageStatus.Delivered, IsRead = true });
                }
            }
            state.LastAppliedIndex = entry.Index; await db.SaveChangesAsync(ct);
        }
    }
}
