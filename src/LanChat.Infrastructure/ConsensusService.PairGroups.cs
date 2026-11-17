using System.Text.Json;
using LanChat.Application;
using LanChat.Domain;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LanChat.Infrastructure;

internal sealed class PairGroupMaintenance(ConsensusService consensus) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => consensus.MaintainPairGroupsAsync(stoppingToken);
}

public sealed partial class ConsensusService
{
    private sealed record ConnectedPair(PeerNode Peer, Guid CoordinatorNodeId, bool CanCreateGroup);
    private volatile ConnectedPair? _pair;

    private PeerNode? GetPairPeer(Membership configuration, CancellationToken ct)
    {
        if (_nodeId == Guid.Empty || options.BootstrapNodeId == Guid.Empty || faults.SimulateLeaderFailure) return null;
        var peer = store.GetSoleOnlinePeer(ct);
        return peer is not null && !configuration.Quorum([_nodeId, peer.NodeId]) ? peer : null;
    }

    private string PairStatusText(ConnectedPair pair)
    {
        var status = pair.CoordinatorNodeId == _nodeId ? "Máy này đang điều phối hai máy" : "Đã kết nối máy điều phối hai máy";
        return status + (pair.CanCreateGroup ? " · sửa nhóm chờ đủ phiếu" : " · tạo và sửa nhóm chờ đủ phiếu");
    }

    internal async Task MaintainPairGroupsAsync(CancellationToken ct)
    {
        if (options.BootstrapNodeId == Guid.Empty) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await GetStatusAsync(ct).ConfigureAwait(false);
                if (status.LeaderReady)
                {
                    _pair = null;
                    if (ReadPendingCreation(null, ct) is { } pending)
                        await SubmitSignedAsync(status, pending, ct).ConfigureAwait(false);
                }
                else
                {
                    _pair = await RefreshPairAsync(status, ct).ConfigureAwait(false);
                    if (_pair is { CanCreateGroup: true } pair && ReadPendingCreation(pair.Peer.NodeId, ct) is { } pending)
                        await DeliverPairGroupAsync(pair.Peer, pending, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsNetworkError(ex))
            { logger.LogDebug("Pair group synchronization deferred: {Type}", ex.GetType().Name); }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }

    private async Task<ConnectedPair?> RefreshPairAsync(ConsensusStatus status, CancellationToken ct)
    {
        if (status.SoleOnlinePeerNodeId is not Guid peerId) return null;
        var peer = store.GetSoleOnlinePeer(ct);
        if (peer is null || peer.NodeId != peerId) return null;

        ConsensusStatus remote;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(650);
        try
        {
            remote = await transport.CallAsync<ConsensusStatus>(new(peer.NodeId, peer.IPAddress, peer.ServicePort),
                "ConsensusStatus", new { }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsNetworkError(ex)) { return null; }

        if (remote.LeaderReady || (remote.SupportsPairGroups && remote.SoleOnlinePeerNodeId != _nodeId)) return null;
        // The confirmed pair agrees on one coordinator. This does not grant a vote or change the control log.
        if (runtime.CanLead && (!remote.SupportsPairGroups || !remote.CanLead || _nodeId.CompareTo(peer.NodeId) < 0))
            return new(peer, _nodeId, remote.SupportsPairGroups);
        return remote.CanLead ? new(peer, peer.NodeId, remote.SupportsPairGroups) : null;
    }

    internal async Task<ControlResult> SavePairGroupAsync(ControlCommand command, Guid source, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = await ReadAsync(ct).ConfigureAwait(false);
            var peer = GetPairPeer(Configuration(snapshot.Log), ct);
            var pair = _pair;
            if (peer is null || pair is not { CanCreateGroup: true } || pair.Peer.NodeId != peer.NodeId ||
                CanPropose(snapshot) || (_remoteReady && FreshLeader(snapshot)))
                return new(false, "Hai máy chưa sẵn sàng tạo nhóm.");

            var local = await store.GetIdentityAsync(ct).ConfigureAwait(false);
            var fromPeer = source == peer.NodeId;
            if ((source != _nodeId && !fromPeer) || command.OriginNodeId != source || command.ActorUserId != (fromPeer ? peer.UserId : local.UserId) ||
                command.Type != ControlCommandType.GroupCreated || command.Id == Guid.Empty || command.GroupId == Guid.Empty ||
                string.IsNullOrWhiteSpace(command.Name) || command.Name!.Length > 100 || command.AvatarBase64 is not null ||
                command.TargetUserId.HasValue || command.OldVoters is not null || command.NewVoters is not null ||
                command.Members is not { Length: 2 } members || !members.Contains(local.UserId) || !members.Contains(peer.UserId))
                return new(false, "Nhóm mới cần đúng hai người dùng đang kết nối.");

            return await store.WriteAsync(db =>
            {
                if (IsPendingCreation(db, command)) return Task.FromResult(new ControlResult(true, null));
                if (db.Exists<Group>("Id=$0", command.GroupId) || db.Exists<Conversation>("Id=$0 AND Kind=$1", command.GroupId, ConversationKind.Direct) ||
                    db.Scalar("SELECT EXISTS(SELECT 1 FROM LegacyGroupOwners WHERE GroupId=$0)", command.GroupId) != 0)
                    return Task.FromResult(new ControlResult(false, "Nhóm đã tồn tại."));

                store.VerifyGroupCommand(db, command, null);
                ProjectNewGroup(db, command);
                db.Execute("INSERT INTO PendingPairGroups VALUES ($0,$1,$2,$3,$4)", command.GroupId, source, peer.NodeId,
                    JsonSerializer.Serialize(command), fromPeer);
                return Task.FromResult(new ControlResult(true, null));
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private ControlCommand? ReadPendingCreation(Guid? recipient, CancellationToken ct)
    {
        using var db = database.Open(ct);
        using var query = db.Command("SELECT CommandJson FROM PendingPairGroups WHERE OriginNodeId=$0 " +
            "AND ($1 IS NULL OR (PeerNodeId=$1 AND Delivered=0)) ORDER BY rowid LIMIT 1", _nodeId, recipient);
        return query.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ControlCommand>(json) : null;
    }

    private async Task DeliverPairGroupAsync(PeerNode peer, ControlCommand command, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var result = await transport.CallAsync<ControlResult>(new(peer.NodeId, peer.IPAddress, peer.ServicePort),
            "PairGroupCreate", command, timeout.Token).ConfigureAwait(false);
        if (result.Success)
            await store.WriteAsync(db => Task.FromResult(db.Execute("UPDATE PendingPairGroups SET Delivered=1 WHERE GroupId=$0",
                command.GroupId)), ct).ConfigureAwait(false);
    }

    private static bool IsPendingCreation(ChatDbContext db, ControlCommand command) => command.Type == ControlCommandType.GroupCreated &&
        db.Scalar("SELECT EXISTS(SELECT 1 FROM PendingPairGroups WHERE GroupId=$0 AND CommandJson=$1)",
            command.GroupId, JsonSerializer.Serialize(command)) != 0;
}
