using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;

namespace LanChat.Infrastructure;

public sealed partial class ConsensusService
{
    private async Task<string?> ValidateGroupCommandAsync(ControlCommand command, CancellationToken ct)
    {
        if (!Enum.IsDefined(typeof(ControlCommandType), command.Type)) return "Thao tác nhóm không hợp lệ.";
        if (command.Id == Guid.Empty || command.GroupId == Guid.Empty || command.ActorUserId == Guid.Empty) return "Định danh thao tác không hợp lệ.";
        if (command.Type is ControlCommandType.GroupCreated or ControlCommandType.GroupRenamed &&
            (string.IsNullOrWhiteSpace(command.Name) || command.Name!.Length > 100)) return "Tên nhóm cần từ 1 đến 100 ký tự.";
        if (command.Type == ControlCommandType.GroupAvatarChanged || command.AvatarBase64 is not null) return "LanChat dùng chữ cái đại diện cho nhóm.";
        using var db = database.Open(ct);
        if (command.Type == ControlCommandType.GroupCreated)
        {
            if (db.Exists<Group>("Id=$0", command.GroupId)) return "Nhóm đã tồn tại.";
            if (command.Members is null || command.Members.Length > 100 || !command.Members.Contains(command.ActorUserId)) return "Danh sách thành viên không hợp lệ.";
            var known = db.Rows<LocalUserProfile>("Id IN (" + ChatDbContext.Parameters(command.Members.Length) + ")", command.Members.Cast<object>().ToArray()).Select(x => x.Id).ToArray();
            if (command.Members.Except(known).Any()) return "Thông tin thành viên chưa được đồng bộ. Vui lòng thử lại sau.";
            return null;
        }
        var group = db.One<Group>("Id=$0 AND IsDeleted=0", command.GroupId);
        if (group is null) return "Nhóm không tồn tại hoặc đã bị xóa.";
        if (group.OwnerUserId != command.ActorUserId) return "Chỉ chủ nhóm được thay đổi cấu trúc nhóm.";
        if (command.Type is ControlCommandType.GroupMemberAdded or ControlCommandType.GroupMemberRemoved or ControlCommandType.GroupOwnerChanged)
        {
            if (command.TargetUserId is null || command.TargetUserId == Guid.Empty) return "Chưa chọn người dùng.";
            if (!db.Exists<LocalUserProfile>("Id=$0", command.TargetUserId)) return "Người dùng chưa được đồng bộ.";
            if (command.Type == ControlCommandType.GroupMemberAdded && db.Count<GroupMember>("GroupId=$0", group.Id) >= 100) return "Nhóm tối đa 100 thành viên.";
            if (command.Type == ControlCommandType.GroupMemberRemoved && command.TargetUserId == group.OwnerUserId) return "Hãy chuyển quyền chủ nhóm trước khi xóa chủ nhóm.";
            if (command.Type == ControlCommandType.GroupOwnerChanged && !db.Exists<GroupMember>("GroupId=$0 AND UserId=$1", group.Id, command.TargetUserId)) return "Chủ nhóm mới phải là thành viên hiện tại.";
        }
        return null;
    }
    private async Task ApplyCommittedAsync(ChatDbContext db, ClusterState state, CancellationToken ct)
    {
        var entries = db.Rows<ControlLogEntry>("\"Index\">$0 AND \"Index\"<=$1 ORDER BY \"Index\"", state.LastAppliedIndex, state.CommitIndex);
        foreach (var entry in entries)
        {
            if (entry.Index != state.LastAppliedIndex + 1) throw new InvalidDataException("Thiếu control log trước khi apply.");
            var c = JsonSerializer.Deserialize<ControlCommand>(entry.CommandJson)!;
            if (c.Type >= ControlCommandType.GroupCreated)
            {
                var group = db.One<Group>("Id=$0", c.GroupId);
                if (c.Type == ControlCommandType.GroupCreated && group is null)
                {
                    group = new Group { Id = c.GroupId, ConversationId = c.GroupId, Name = c.Name!, OwnerUserId = c.ActorUserId, AvatarBase64 = c.AvatarBase64 };
                    db.Insert(group);
                    var conversation = db.One<Conversation>("Id=$0", c.GroupId);
                    if (conversation is null) db.Insert(new Conversation { Id = c.GroupId, Kind = ConversationKind.Group, Title = c.Name! });
                    else { conversation.Kind = ConversationKind.Group; conversation.Title = c.Name!; }
                    foreach (var user in c.Members!.Distinct())
                    {
                        db.Insert(new GroupMember { Id = LanChat.Domain.AppIdentity.NewId(), GroupId = c.GroupId, UserId = user });
                        if (!db.Exists<ConversationMember>("ConversationId=$0 AND UserId=$1", c.GroupId, user))
                            db.Insert(new ConversationMember { Id = LanChat.Domain.AppIdentity.NewId(), ConversationId = c.GroupId, UserId = user });
                    }
                }
                else if (group is not null)
                {
                    switch (c.Type)
                    {
                        case ControlCommandType.GroupRenamed:
                            group.Name = c.Name!; db.Required<Conversation>("Id=$0", group.Id).Title = c.Name!; break;
                        case ControlCommandType.GroupAvatarChanged: group.AvatarBase64 = c.AvatarBase64; break;
                        case ControlCommandType.GroupOwnerChanged: group.OwnerUserId = c.TargetUserId!.Value; break;
                        case ControlCommandType.GroupDeleted: group.IsDeleted = true; break;
                        case ControlCommandType.GroupMemberAdded:
                            if (!db.Exists<GroupMember>("GroupId=$0 AND UserId=$1", group.Id, c.TargetUserId))
                            {
                                db.Insert(new GroupMember { Id = LanChat.Domain.AppIdentity.NewId(), GroupId = group.Id, UserId = c.TargetUserId!.Value });
                                db.Insert(new ConversationMember { Id = LanChat.Domain.AppIdentity.NewId(), ConversationId = group.Id, UserId = c.TargetUserId.Value });
                            }
                            break;
                        case ControlCommandType.GroupMemberRemoved:
                            db.Delete<GroupMember>("GroupId=$0 AND UserId=$1", group.Id, c.TargetUserId);
                            db.Delete<ConversationMember>("ConversationId=$0 AND UserId=$1", group.Id, c.TargetUserId); break;
                    }
                }
                if (!db.Exists<Message>("Id=$0", c.Id))
                {
                    var text = c.Type switch
                    {
                        ControlCommandType.GroupCreated => $"Đã tạo nhóm {c.Name}", ControlCommandType.GroupRenamed => $"Tên nhóm được đổi thành {c.Name}",
                        ControlCommandType.GroupMemberAdded => "Đã thêm thành viên vào nhóm", ControlCommandType.GroupMemberRemoved => "Đã xóa thành viên khỏi nhóm",
                        ControlCommandType.GroupOwnerChanged => "Đã chuyển quyền chủ nhóm", ControlCommandType.GroupDeleted => "Nhóm đã bị xóa", _ => "Đã cập nhật ảnh nhóm"
                    };
                    db.Insert(new Message { Id = c.Id, ClientMessageId = c.Id, ConversationId = c.GroupId, SenderUserId = Guid.Empty,
                        OriginNodeId = DirectConversation.GetId(options.ClusterId, Guid.Empty, Guid.Empty), OriginSequence = entry.Index,
                        Text = text, Kind = MessageKind.System, CreatedAtTicks = c.CreatedAtTicks, PhysicalTicks = c.CreatedAtTicks, Status = MessageStatus.Delivered, IsRead = true });
                }
            }
            state.LastAppliedIndex = entry.Index; db.SaveChanges();
        }
    }
}
