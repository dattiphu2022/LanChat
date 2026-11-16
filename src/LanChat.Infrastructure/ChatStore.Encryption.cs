using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;

namespace LanChat.Infrastructure;

public sealed partial class ChatStore
{
    private DeviceIdentity? _device;
    private DeviceIdentity Device => _device ?? throw new InvalidOperationException("Khóa thiết bị chưa được khởi tạo.");

    private async Task InitializeDeviceAsync(CancellationToken ct)
    {
        if (_device is not null) return;
        DeviceIdentity? loaded = null;
        try
        {
            await WriteAsync(db =>
            {
                var local = db.Required<LocalSettings>();
                loaded = DeviceIdentity.LoadOrCreate(db, options.ClusterId);
                PinDevice(db, local.NodeId, local.UserId, loaded.PublicKeys);
                return Task.FromResult(true);
            }, ct).ConfigureAwait(false);
            _device = loaded;
        }
        catch { loaded?.Dispose(); throw; }
    }

    private static DevicePublicKeys? FindDeviceKeys(ChatDbContext db, Guid userId)
    {
        using var query = db.Command("SELECT EncryptionKey,SigningKey FROM TrustedDevices WHERE UserId=$0", userId);
        using var reader = query.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1)) : null;
    }

    private static void PinDevice(ChatDbContext db, Guid nodeId, Guid userId, DevicePublicKeys keys)
    {
        if (nodeId == Guid.Empty || userId == Guid.Empty) throw new InvalidDataException("Định danh thiết bị không hợp lệ.");
        using (var query = db.Command("SELECT NodeId,UserId,EncryptionKey,SigningKey FROM TrustedDevices WHERE NodeId=$0 OR UserId=$1", nodeId, userId))
        using (var reader = query.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetGuid(0) != nodeId || reader.GetGuid(1) != userId ||
                    reader.GetString(2) != keys.EncryptionKey || reader.GetString(3) != keys.SigningKey)
                    throw new InvalidDataException("Khóa định danh thiết bị đã thay đổi.");
                return;
            }
        }
        using var encryptionKey = DeviceIdentity.ImportPublicEncryptionKey(keys.EncryptionKey);
        db.Execute("INSERT INTO TrustedDevices VALUES ($0,$1,$2,$3)", userId, nodeId, keys.EncryptionKey, keys.SigningKey);
    }

    private void VerifyPeerIdentity(ChatDbContext db, NodeInfoDto info)
    {
        DeviceIdentity.Verify(options.ClusterId, "discovery", info with { KeyProof = null }, info.DeviceKeys, info.KeyProof);
        PinDevice(db, info.NodeId, info.User.Id, info.DeviceKeys!);
    }

    public Task<EncryptionIdentity[]> GetEncryptionIdentitiesAsync(CancellationToken ct = default)
    {
        using var db = database.Open(ct);
        var local = db.Required<LocalSettings>().UserId;
        using var query = db.Command("SELECT t.UserId,u.DisplayName,t.EncryptionKey,t.SigningKey FROM TrustedDevices t JOIN Users u ON u.Id=t.UserId ORDER BY u.DisplayName");
        using var reader = query.ExecuteReader();
        var identities = new List<EncryptionIdentity>();
        while (reader.Read())
        {
            var id = reader.GetGuid(0);
            var fingerprint = BitConverter.ToString(FrameworkIO.Hash(Convert.FromBase64String(reader.GetString(2))
                .Concat(Convert.FromBase64String(reader.GetString(3))).ToArray())).Replace("-", "");
            identities.Add(new(id, reader.GetString(1), fingerprint, id == local));
        }
        return Task.FromResult(identities.ToArray());
    }

    private byte[] EventEncryptionContext(Guid id) => Encoding.ASCII.GetBytes($"LanChat-v4/event/{options.ClusterId:N}/{id:N}");

    private string EncryptEvent<T>(ChatDbContext db, Guid eventId, T payload, Guid[] recipients)
    {
        var keys = new List<(Guid UserId, DevicePublicKeys Keys)>();
        foreach (var user in recipients.Distinct().OrderBy(id => id))
        {
            var key = FindDeviceKeys(db, user);
            if (key is null)
            {
                var name = db.One<LocalUserProfile>("Id=$0", user)?.DisplayName ?? "người nhận";
                throw new InvalidOperationException($"Chưa có khóa mã hóa của {name}. Hãy mở LanChat phiên bản mới trên thiết bị đó để đồng bộ khóa.");
            }
            keys.Add((user, key));
        }
        if (keys.Count is < 1 or > 100) throw new InvalidOperationException("Số người nhận mã hóa không hợp lệ.");
        return DeviceIdentity.Seal(JsonSerializer.SerializeToUtf8Bytes(payload), keys, EventEncryptionContext(eventId));
    }

    private T? ReadEncryptedEvent<T>(ReplicationEvent item, Guid userId) where T : class
    {
        var opened = Device.Open(DeviceIdentity.ParseEnvelope(item.PayloadJson), userId, EventEncryptionContext(item.Id));
        if (opened is null) return null;
        try { return JsonSerializer.Deserialize<T>(opened) ?? throw new InvalidDataException("Thiếu nội dung đã mã hóa."); }
        finally { Array.Clear(opened, 0, opened.Length); }
    }

    private Guid[] ActiveMessageAudience(ChatDbContext db, Guid messageId, Guid conversationId)
    {
        var created = db.One<ReplicationEvent>("EntityId=$0 AND Type=$1 ORDER BY PhysicalTicks,LogicalCounter,OriginNodeId,Id LIMIT 1",
            messageId, ReplicationEventType.MessageCreated) ?? throw new InvalidOperationException("Không tìm thấy bản mã gốc của tin nhắn.");
        var audience = new HashSet<Guid>(DeviceIdentity.ParseEnvelope(created.PayloadJson).Recipients.Select(x => x.UserId));
        return db.Rows<ConversationMember>("ConversationId=$0", conversationId).Select(x => x.UserId).Where(audience.Contains).ToArray();
    }

    internal ControlCommand SignGroupCommand(ControlCommand command, Guid nodeId, CancellationToken ct)
    {
        using var db = database.Open(ct);
        var issued = command with { OriginNodeId = nodeId, CreatedAtTicks = DateTime.UtcNow.Ticks,
            PreviousCommandId = db.One<Group>("Id=$0", command.GroupId)?.LastCommandId, DeviceKeys = Device.PublicKeys, Signature = null };
        return issued with { Signature = Device.Sign(options.ClusterId, "group", issued) };
    }

    internal void VerifyGroupCommand(ChatDbContext db, ControlCommand command, Group? current)
    {
        DeviceIdentity.Verify(options.ClusterId, "group", command with { Signature = null }, command.DeviceKeys, command.Signature);
        PinDevice(db, command.OriginNodeId, command.ActorUserId, command.DeviceKeys!);
        if (command.CreatedAtTicks < 1 || command.CreatedAtTicks > DateTime.MaxValue.Ticks ||
            command.PreviousCommandId != current?.LastCommandId || (command.Type == ControlCommandType.GroupCreated
                ? current is not null : current is null || current.IsDeleted || current.OwnerUserId != command.ActorUserId))
            throw new InvalidDataException("Chữ ký hoặc thứ tự thay đổi nhóm không hợp lệ.");
        if (command.Type == ControlCommandType.GroupCreated &&
            db.Scalar("SELECT EXISTS(SELECT 1 FROM LegacyGroupOwners WHERE GroupId=$0 AND OwnerUserId<>$1)", command.GroupId, command.ActorUserId) != 0)
            throw new InvalidDataException("Chỉ chủ nhóm đã lưu được khôi phục nhóm sau khi nâng cấp.");
    }

    internal Task RegisterFileKeyAsync(string hash, string? encoded, CancellationToken ct)
    {
        var key = DeviceIdentity.Decode(encoded, DeviceIdentity.KeyBytes);
        try
        {
            var id = FileTransferCrypto.TransferId(key);
            var wrapped = ProtectedData.Protect(key, options.ClusterId.ToByteArray(), DataProtectionScope.CurrentUser);
            return WriteAsync(db =>
            {
                RegisterFileKey(db, hash, id, wrapped);
                return Task.FromResult(true);
            }, ct);
        }
        finally { Array.Clear(key, 0, key.Length); }
    }

    private static void RegisterFileKey(ChatDbContext db, string hash, string id, byte[] protectedKey)
    {
        db.Execute("INSERT OR IGNORE INTO FileTransferKeys VALUES ($0,$1,$2)", id, hash, protectedKey);
        if (db.Scalar("SELECT EXISTS(SELECT 1 FROM FileTransferKeys WHERE Id=$0 AND Sha256=$1)", id, hash) == 0)
            throw new InvalidDataException("Khóa tệp không khớp nội dung.");
    }

    internal (string Hash, byte[] Key)? FindFileKey(string transferId, CancellationToken ct)
    {
        if (transferId is null || transferId.Length != 64 || !transferId.All(Uri.IsHexDigit)) return null;
        using var db = database.Open(ct);
        using var query = db.Command("SELECT Sha256,ProtectedKey FROM FileTransferKeys WHERE Id=$0", transferId);
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetString(0), ProtectedData.Unprotect((byte[])reader.GetValue(1), options.ClusterId.ToByteArray(), DataProtectionScope.CurrentUser));
    }
}
