using System.Collections.Concurrent;
using System.Diagnostics;
using LanChat.Application;
using LanChat.Domain;

namespace LanChat.Infrastructure;

public sealed partial class ChatStore
{
    private static readonly TimeSpan PeerPresenceWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PresenceWriteInterval = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<Guid, long> _lastConnectionWrites = new();

    internal enum PeerObservation { Discovery, Connection, VerifiedAddress }

    public Task RememberPeerAsync(NodeInfoDto peer, string address, CancellationToken ct = default) =>
        ObservePeerAsync(peer, address, PeerObservation.VerifiedAddress, ct);

    internal Task ObservePeerAsync(NodeInfoDto peer, string address, PeerObservation observation, CancellationToken ct) =>
        WriteAsync(db =>
        {
            var local = db.Required<LocalSettings>();
            if (peer.NodeId == local.NodeId || peer.User?.Id == local.UserId) return Task.FromResult(false);
            UpsertPeer(db, peer, address, observation);
            return Task.FromResult(true);
        }, ct);

    private void UpsertPeer(ChatDbContext db, NodeInfoDto peer, string address, PeerObservation observation)
    {
        if (peer.ProtocolVersion != NodeInfoDto.CurrentProtocol || peer.User is null || peer.NodeId == Guid.Empty || peer.User.Id == Guid.Empty ||
            peer.ServicePort is < 1024 or > 65535 || string.IsNullOrWhiteSpace(peer.User.DisplayName) || peer.User.DisplayName.Length > 100)
            throw new InvalidDataException("Thông tin peer không hợp lệ.");

        VerifyPeerIdentity(db, peer);
        var now = DateTime.UtcNow.Ticks;
        var known = db.One<PeerNode>("NodeId=$0", peer.NodeId);
        var isNew = known is null;
        if (known is null)
        {
            known = new PeerNode { Id = AppIdentity.NewId(), NodeId = peer.NodeId, IPAddress = address, ServicePort = peer.ServicePort };
            db.Insert(known);
        }
        var addressChanged = observation == PeerObservation.VerifiedAddress && (known.IPAddress != address || known.ServicePort != peer.ServicePort);
        if (addressChanged)
        {
            known.IPAddress = address;
            known.ServicePort = peer.ServicePort;
        }
        var routeChanged = db.Scalar("SELECT EXISTS(SELECT 1 FROM PeerAddresses WHERE NodeId=$0 AND IPAddress=$1 AND ServicePort=$2)",
            peer.NodeId, address, peer.ServicePort) == 0;
        db.Execute("INSERT INTO PeerAddresses (NodeId,IPAddress,ServicePort,LastObservedTicks) VALUES ($0,$1,$2,$3) " +
            "ON CONFLICT (NodeId,IPAddress) DO UPDATE SET ServicePort=excluded.ServicePort,LastObservedTicks=excluded.LastObservedTicks",
            peer.NodeId, address, peer.ServicePort, now);
        var connected = observation != PeerObservation.Discovery;
        if (isNew || routeChanged || addressChanged || (connected && known.LastSeenTicks < now - PeerPresenceWindow.Ticks)) db.NotePresenceChange();
        if (connected) known.LastSeenTicks = now;
        known.UserId = peer.User.Id; known.DeviceName = peer.DeviceName;
        known.Role = peer.Role; known.Term = peer.Term; known.ApplicationVersion = peer.ApplicationVersion;

        var user = db.One<LocalUserProfile>("Id=$0", peer.User.Id);
        if (user is null) { user = new LocalUserProfile { Id = peer.User.Id }; db.Insert(user); }
        user.DisplayName = peer.User.DisplayName; user.FullName = peer.User.FullName; user.Nickname = peer.User.Nickname;
        user.Department = peer.User.Department; user.Position = peer.User.Position; user.Phone = peer.User.Phone; user.StatusMessage = peer.User.StatusMessage;
    }

    internal PeerEndpoint GetPreferredPeerEndpoint(PeerEndpoint peer, CancellationToken ct)
    {
        using var db = database.Open(ct);
        var known = db.One<PeerNode>("NodeId=$0", peer.NodeId);
        return known is null ? peer : new(known.NodeId, known.IPAddress, known.ServicePort);
    }

    internal PeerEndpoint[] GetAlternatePeerEndpoints(PeerEndpoint preferred, CancellationToken ct)
    {
        using var db = database.Open(ct);
        using var command = db.Command("SELECT IPAddress,ServicePort FROM PeerAddresses WHERE NodeId=$0 AND (IPAddress<>$1 OR ServicePort<>$2) " +
            "ORDER BY LastObservedTicks DESC,IPAddress LIMIT 4", preferred.NodeId, preferred.Address, preferred.Port);
        using var reader = command.ExecuteReader();
        var endpoints = new List<PeerEndpoint>();
        while (reader.Read()) endpoints.Add(new(preferred.NodeId, reader.GetString(0), reader.GetInt32(1)));
        return endpoints.ToArray();
    }

    internal PeerEndpoint[] GetActivePeerEndpoints(CancellationToken ct)
    {
        using var db = database.Open(ct);
        return ReadPeerEndpoints(db, "LastSeenTicks>=$0 OR NodeId IN (SELECT NodeId FROM PeerAddresses WHERE LastObservedTicks>=$0)",
            DateTime.UtcNow.Ticks - PeerPresenceWindow.Ticks);
    }

    private static PeerEndpoint[] ReadPeerEndpoints(ChatDbContext db, string where, params object[] args) =>
        db.Rows<PeerNode>(where + " ORDER BY LastSeenTicks DESC", args).Select(x => new PeerEndpoint(x.NodeId, x.IPAddress, x.ServicePort)).ToArray();

    internal async Task NotePeerConnectionAsync(Guid nodeId, CancellationToken ct, PeerEndpoint? previous = null, PeerEndpoint? connected = null)
    {
        var changedAddress = connected is not null && previous is not null && connected != previous;
        if (!changedAddress && _lastConnectionWrites.TryGetValue(nodeId, out var updated) && FrameworkIO.Elapsed(updated) < PresenceWriteInterval)
            return;
        var recorded = await WriteAsync(db =>
        {
            var known = db.One<PeerNode>("NodeId=$0", nodeId);
            if (known is null) return Task.FromResult(false);

            var now = DateTime.UtcNow.Ticks;
            if (known.LastSeenTicks < now - PeerPresenceWindow.Ticks) db.NotePresenceChange();
            if (known.LastSeenTicks < now - PresenceWriteInterval.Ticks) known.LastSeenTicks = now;
            if (changedAddress && known.IPAddress == previous!.Address && known.ServicePort == previous.Port)
            {
                known.IPAddress = connected!.Address;
                known.ServicePort = connected.Port;
                db.NotePresenceChange();
            }
            return Task.FromResult(true);
        }, ct).ConfigureAwait(false);
        if (recorded) _lastConnectionWrites[nodeId] = Stopwatch.GetTimestamp();
    }
}
